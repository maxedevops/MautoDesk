# OCR worker — Python

**Status: not yet implemented.** Release 2, and gated on `RISK-LEGAL-002`.

A separate Python 3.12 service running OpenCV + PaddleOCR (ADR-0003). It exists as its own process
because Tesseract-class .NET options are materially worse on the document types that matter here —
titles, registrations, and driver's licences photographed on a phone under fluorescent light.

## Pipeline

```
.NET worker → signed URL for ONE object → ocr-worker
                                            ↓
                            OpenCV preprocessing (deskew, denoise, threshold)
                                            ↓
                                    PaddleOCR → text + per-block bbox + confidence
                                            ↓
              back to .NET → LLM field extraction → validation → JSON → database
```

## Security posture: no ambient credentials

The worker is **stateless**. It has no database access and no storage credentials — only a
short-lived signed URL for the single object it is processing. If it is compromised, the blast radius
is that one document, not the document vault.

LLM field extraction happens back in .NET rather than here, so prompt handling, cost metering, quota
enforcement, and audit all stay in one place (ADR-0004).

## What gets retained

Per the constitution: the original image, the raw OCR text, the structured extraction, any human
corrections, and confidence scores. Corrections are stored **alongside** the extraction, never
overwriting it — the model's original output is evidence of what the system actually read.

Low-confidence extractions become a review queue, not a silent acceptance.

## Blocking legal review

**Several states restrict capturing, retaining, and using driver's licence data**, and biometric
laws can attach to face images. Before this module ships in any state:

- purpose limitation and no secondary use, enforced in code
- configurable, per-tenant retention with a default that is short
- per-tenant opt-in rather than on-by-default
- attorney review of the retention and use posture

Tracked as `RISK-LEGAL-002` in `docs/02-architecture.md` §12. This is not a checkbox — it is the
reason this module is not in Release 1.
