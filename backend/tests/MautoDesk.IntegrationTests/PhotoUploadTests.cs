using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The quarantine-first upload pipeline, end to end against real storage.
/// </summary>
/// <remarks>
/// <para>
/// These tests upload to MinIO exactly as a browser would: request an intent,
/// PUT to the presigned URL, then confirm. Nothing is stubbed, because the part
/// worth testing <em>is</em> the interaction — a mock object store would happily
/// agree that a text file is a JPEG.
/// </para>
/// <para>
/// Requires the compose stack's <c>minio</c> and <c>minio-init</c> services.
/// Point them somewhere else with <c>TEST_STORAGE_URL</c>.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class PhotoUploadTests
{
    private const string PhotoWrite = "inventory.photo.write";
    private const string VehicleRead = "inventory.vehicle.read";
    private const string VehicleWrite = "inventory.vehicle.write";

    private readonly ApiFixture _fixture;

    public PhotoUploadTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_photo_is_verified_re_encoded_and_promoted()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48230");
        var image = Jpeg(1200, 900);

        var photo = await UploadAsync(client, vehicle, image);

        photo.GetProperty("status").GetString().Should().Be("ready");
        photo.GetProperty("url").GetString().Should().NotBeNullOrWhiteSpace();
        photo.GetProperty("thumbnailUrl").GetString().Should().NotBeNullOrWhiteSpace();
        photo.GetProperty("width").GetInt32().Should().Be(1200);
        photo.GetProperty("height").GetInt32().Should().Be(900);
    }

    /// <summary>
    /// The promoted object is our re-encode, not the file that was uploaded.
    /// </summary>
    /// <remarks>
    /// This is the assertion behind "EXIF is stripped": if the stored bytes were
    /// the uploaded bytes, every metadata guarantee in ADR-0005 would be a
    /// comment rather than a control.
    /// </remarks>
    [Fact]
    public async Task The_stored_object_is_a_re_encode_rather_than_the_uploaded_file()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48231");
        var image = Jpeg(800, 600);

        var photo = await UploadAsync(client, vehicle, image);

        using var download = new HttpClient();
        var stored = await download.GetByteArrayAsync(new Uri(photo.GetProperty("url").GetString()!));

        stored.Should().NotEqual(image, "the promoted object must be our encode, not the client's file");
        SKBitmap.Decode(stored).Should().NotBeNull("and it must still be a valid image");
    }

    /// <summary>
    /// A file whose bytes do not match the declared digest never becomes a photo.
    /// </summary>
    [Fact]
    public async Task A_digest_that_does_not_match_the_upload_is_rejected()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48232");

        var declared = Jpeg(400, 300);
        var actual = Jpeg(400, 301);

        // Same length, different content: the size check passes and the digest
        // check is the one that has to catch it.
        var padded = Pad(actual, declared.Length);

        var intent = await RequestIntentAsync(client, vehicle, "image/jpeg", declared.Length, Digest(declared));
        await PutAsync(intent, padded, "image/jpeg");

        var response = await ConfirmAsync(client, vehicle, intent.PhotoId);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("photo.digest_mismatch");

        (await StatusOfAsync(client, vehicle, intent.PhotoId)).Should().Be("rejected");
    }

    /// <summary>
    /// A text file named as a JPEG is still a text file.
    /// </summary>
    /// <remarks>
    /// The declared content type is the client's word for it. This is the check
    /// that makes the word irrelevant: the bytes have to decode as an image.
    /// </remarks>
    [Fact]
    public async Task A_file_that_is_not_an_image_is_rejected_whatever_it_claims_to_be()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48233");

        var content = Encoding.UTF8.GetBytes("<?php system($_GET['c']); ?>");

        var intent = await RequestIntentAsync(client, vehicle, "image/jpeg", content.Length, Digest(content));
        await PutAsync(intent, content, "image/jpeg");

        var response = await ConfirmAsync(client, vehicle, intent.PhotoId);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("photo.not_an_image");
        (await StatusOfAsync(client, vehicle, intent.PhotoId)).Should().Be("rejected");
    }

    [Fact]
    public async Task Confirming_a_photo_that_was_never_uploaded_is_refused()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48234");
        var image = Jpeg(100, 100);

        var intent = await RequestIntentAsync(client, vehicle, "image/jpeg", image.Length, Digest(image));

        // The client asked for a URL and then went to lunch.
        var response = await ConfirmAsync(client, vehicle, intent.PhotoId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("photo.missing");
    }

    [Fact]
    public async Task A_type_outside_the_allowlist_is_refused_before_a_url_is_issued()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48235");

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/photos", UriKind.Relative),
            new { contentType = "application/pdf", byteSize = 1024, sha256 = Digest([1, 2, 3]) });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("photo.content_type");
    }

    [Fact]
    public async Task Uploading_requires_the_photo_permission()
    {
        // Can read and write vehicles, but not photos.
        var client = await ClientAsync(VehicleRead, VehicleWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48236");
        var image = Jpeg(100, 100);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/photos", UriKind.Relative),
            new { contentType = "image/jpeg", byteSize = image.Length, sha256 = Digest(image) });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A photo belonging to another tenant is not addressable, even with its id.
    /// </summary>
    [Fact]
    public async Task Another_tenants_photo_cannot_be_confirmed_or_seen()
    {
        var owner = await ClientAsync(ApiFixture.TenantA, VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(owner, "1FTFW1ET5MFA48237");
        var photo = await UploadAsync(owner, vehicle, Jpeg(300, 200));
        var photoId = photo.GetProperty("id").GetString()!;

        var outsider = await ClientAsync(ApiFixture.TenantB, VehicleRead, VehicleWrite, PhotoWrite);

        var confirm = await outsider.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/photos/{photoId}/confirm", UriKind.Relative),
            new { });

        // 404, not 403: telling them it exists would confirm another dealer's
        // stock number by trial.
        confirm.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var list = await outsider.GetAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/photos", UriKind.Relative));

        // Also 404, not an empty list: an empty list is what a vehicle with no
        // photos returns, and the two must not be distinguishable.
        list.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// The point of the whole feature: a vehicle with a photo can be published.
    /// </summary>
    [Fact]
    public async Task A_vehicle_becomes_publishable_once_it_has_a_photo()
    {
        var client = await ClientAsync(
            VehicleRead, VehicleWrite, PhotoWrite, "inventory.price.write", "inventory.publish");
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48238");

        await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/status", UriKind.Relative),
            new { status = "available", reason = (string?)null });

        // Priced, so the only thing still missing is the photo. Money is a
        // decimal string on the wire, never a JSON number.
        await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/price", UriKind.Relative),
            new { priceType = "list", newPrice = "18995.00", reason = (string?)null });

        var blocked = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/publish", UriKind.Relative), new { });

        blocked.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("vehicle.publish.no_photos");

        await UploadAsync(client, vehicle, Jpeg(1024, 768));

        var published = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/publish", UriKind.Relative), new { });

        published.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "a photo is what stood between this vehicle and a listing");
    }

    [Fact]
    public async Task A_deleted_photo_stops_being_listed()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48239");
        var photo = await UploadAsync(client, vehicle, Jpeg(200, 200));
        var photoId = photo.GetProperty("id").GetString()!;

        var deleted = await client.DeleteAsync(
            new Uri($"/api/v1/vehicles/{vehicle}/photos/{photoId}", UriKind.Relative));

        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await client.GetAsync(new Uri($"/api/v1/vehicles/{vehicle}/photos", UriKind.Relative));
        using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

        body.RootElement.GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task One_photo_at_a_time_is_the_primary()
    {
        var client = await ClientAsync(VehicleRead, VehicleWrite, PhotoWrite);
        var vehicle = await CreateVehicleAsync(client, "1FTFW1ET5MFA48240");

        var first = (await UploadAsync(client, vehicle, Jpeg(300, 300))).GetProperty("id").GetString()!;
        var second = (await UploadAsync(client, vehicle, Jpeg(320, 300))).GetProperty("id").GetString()!;

        await SetPrimaryAsync(client, vehicle, first);
        await SetPrimaryAsync(client, vehicle, second);

        var list = await client.GetAsync(new Uri($"/api/v1/vehicles/{vehicle}/photos", UriKind.Relative));
        using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());

        var primaries = body.RootElement.EnumerateArray()
            .Where(photo => photo.GetProperty("isPrimary").GetBoolean())
            .Select(photo => photo.GetProperty("id").GetString())
            .ToList();

        primaries.Should().ContainSingle().Which.Should().Be(second);
    }

    /* ------------------------------------------------------------ helpers -- */

    private async Task<HttpClient> ClientAsync(params string[] permissions) =>
        await ClientAsync(ApiFixture.TenantA, permissions);

    private async Task<HttpClient> ClientAsync(Guid tenantId, params string[] permissions)
    {
        var user = await _fixture.CreateUserAsync(tenantId, permissions);
        return _fixture.AnonymousClient().WithToken(user.AccessToken);
    }

    private static async Task<string> CreateVehicleAsync(HttpClient client, string vin)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/vehicles", UriKind.Relative),
            new { vin, decodeVin = false });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Runs the whole three-step upload and asserts it succeeded.</summary>
    private static async Task<JsonElement> UploadAsync(HttpClient client, string vehicleId, byte[] image)
    {
        var intent = await RequestIntentAsync(client, vehicleId, "image/jpeg", image.Length, Digest(image));
        await PutAsync(intent, image, "image/jpeg");

        var confirmed = await ConfirmAsync(client, vehicleId, intent.PhotoId);
        confirmed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private static async Task<UploadIntent> RequestIntentAsync(
        HttpClient client,
        string vehicleId,
        string contentType,
        long byteSize,
        string digest)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/photos", UriKind.Relative),
            new { contentType, byteSize, sha256 = digest });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return new UploadIntent(
            body.RootElement.GetProperty("photoId").GetString()!,
            body.RootElement.GetProperty("uploadUrl").GetString()!);
    }

    /// <summary>PUTs to the presigned URL, exactly as a browser would.</summary>
    private static async Task PutAsync(UploadIntent intent, byte[] content, string contentType)
    {
        // The presigned URL must point at the configured endpoint, scheme and
        // all: the SDK will happily sign an https URL for an http MinIO.
        new Uri(intent.UploadUrl).Scheme.Should().Be("http", intent.UploadUrl);

        using var uploader = new HttpClient();
        using var payload = new ByteArrayContent(content);
        payload.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        var response = await uploader.PutAsync(new Uri(intent.UploadUrl), payload);

        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"the presigned upload must succeed: {await response.Content.ReadAsStringAsync()}");
    }

    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string vehicleId, string photoId) =>
        client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/photos/{photoId}/confirm", UriKind.Relative),
            new { });

    private static async Task SetPrimaryAsync(HttpClient client, string vehicleId, string photoId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/vehicles/{vehicleId}/photos/{photoId}/primary", UriKind.Relative),
            new { });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task<string> StatusOfAsync(HttpClient client, string vehicleId, string photoId)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/vehicles/{vehicleId}/photos", UriKind.Relative));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return body.RootElement.EnumerateArray()
            .First(photo => photo.GetProperty("id").GetString() == photoId)
            .GetProperty("status").GetString()!;
    }

    private static string Digest(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Pads or trims to an exact length, to isolate the digest check from the size check.</summary>
    private static byte[] Pad(byte[] content, int length)
    {
        if (content.Length == length)
        {
            return content;
        }

        var padded = new byte[length];
        content.AsSpan(0, Math.Min(content.Length, length)).CopyTo(padded);

        return padded;
    }

    /// <summary>A real JPEG of the requested size, so decoding is a real decode.</summary>
    private static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);

        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.CornflowerBlue);

            // Some structure, so the encoder cannot collapse the whole thing into
            // a handful of bytes and make the size assertions meaningless.
            using var paint = new SKPaint { Color = SKColors.Goldenrod };
            canvas.DrawRect(new SKRect(0, 0, width / 2f, height / 2f), paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);

        return data.ToArray();
    }

    private sealed record UploadIntent(string PhotoId, string UploadUrl);
}
