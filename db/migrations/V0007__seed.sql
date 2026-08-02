--------------------------------------------------------------------------------
-- MautoDesk — V0007 Seed data
--
-- Idempotent. Safe to re-run on every deploy; this is how permission atoms
-- added by a new module reach existing environments.
--
-- IMPORTANT: the OK/KS/TX rule sets seeded here are STRUCTURAL SKELETONS with
-- approved_at = NULL, which means the deal engine will refuse to use them. The
-- rates, caps, and fee schedules must be entered from cited primary sources and
-- signed off by a CPA or dealer-compliance attorney per state before that state
-- goes live. See docs/02-architecture.md §9. Do not paste numbers in here from
-- memory or from a blog post.
--------------------------------------------------------------------------------

--------------------------------------------------------------------------------
-- Permission catalogue
--------------------------------------------------------------------------------
insert into identity.permission (code, module, description, is_sensitive) values
  -- Inventory
  ('inventory.vehicle.read',      'inventory', 'View vehicles',                              false),
  ('inventory.vehicle.write',     'inventory', 'Create and edit vehicles',                   false),
  ('inventory.vehicle.delete',    'inventory', 'Delete vehicles',                            false),
  ('inventory.cost.read',         'inventory', 'View acquisition and reconditioning cost',   true),
  ('inventory.cost.write',        'inventory', 'Record and edit vehicle costs',              true),
  ('inventory.price.write',       'inventory', 'Change asking prices',                       false),
  ('inventory.photo.write',       'inventory', 'Upload and manage vehicle photos',           false),
  ('inventory.recon.write',       'inventory', 'Manage reconditioning steps',                false),
  ('inventory.publish',           'inventory', 'Publish vehicles to website and marketplaces', false),
  -- CRM
  ('crm.customer.read',           'crm',       'View customers',                             false),
  ('crm.customer.write',          'crm',       'Create and edit customers',                  false),
  ('crm.customer.pii.read',       'crm',       'View unmasked SSN, DOB and licence numbers', true),
  ('crm.lead.read',               'crm',       'View leads assigned to the user',            false),
  ('crm.lead.read.all',           'crm',       'View all leads regardless of assignment',    false),
  ('crm.lead.write',              'crm',       'Create and edit leads',                      false),
  ('crm.lead.assign',             'crm',       'Assign leads to users',                      false),
  ('crm.task.write',              'crm',       'Create and complete tasks',                  false),
  ('crm.appointment.write',       'crm',       'Schedule appointments',                      false),
  -- Sales
  ('sales.deal.read',             'sales',     'View deals the user is on',                  false),
  ('sales.deal.read.all',         'sales',     'View all deals',                             false),
  ('sales.deal.write',            'sales',     'Create and edit deals',                      false),
  ('sales.deal.approve',          'sales',     'Approve a deal for contracting',             true),
  ('sales.deal.void',             'sales',     'Cancel or unwind a contracted deal',         true),
  ('sales.deal.finance.read',     'sales',     'View buy rate, reserve and lender terms',    true),
  ('sales.deal.finance.write',    'sales',     'Enter financing terms',                      true),
  ('sales.gross.read',            'sales',     'View gross profit on deals',                 true),
  ('sales.commission.read',       'sales',     'View own commission',                        false),
  ('sales.commission.read.all',   'sales',     'View all commissions',                       true),
  ('sales.commission.approve',    'sales',     'Approve commission for payment',             true),
  ('sales.fee.write',             'sales',     'Configure dealer fees',                      true),
  -- Documents and signatures
  ('documents.read',              'documents', 'View documents',                             false),
  ('documents.write',             'documents', 'Upload and generate documents',              false),
  ('documents.delete',            'documents', 'Delete documents',                           true),
  ('documents.template.write',    'documents', 'Create and edit document templates',         true),
  ('signatures.send',             'signatures','Send documents for signature',               false),
  ('signatures.void',             'signatures','Void a signature envelope',                  true),
  -- Messaging
  ('messaging.read',              'messaging', 'Read customer conversations',                false),
  ('messaging.send',              'messaging', 'Send email and SMS to customers',            false),
  ('messaging.template.write',    'messaging', 'Manage message templates',                   false),
  -- AI
  ('ai.generate',                 'ai',        'Request AI-generated content',               false),
  ('ai.approve',                  'ai',        'Approve AI content for customer-facing use', false),
  -- Reporting
  ('reports.read',                'reporting', 'View standard reports',                      false),
  ('reports.financial.read',      'reporting', 'View financial and profitability reports',   true),
  ('reports.export',              'reporting', 'Export report data',                         true),
  -- Administration
  ('admin.user.read',             'admin',     'View users',                                 false),
  ('admin.user.manage',           'admin',     'Invite, edit, suspend and remove users',     true),
  ('admin.role.manage',           'admin',     'Create roles and assign permissions',        true),
  ('admin.settings.manage',       'admin',     'Change dealership settings',                 true),
  ('admin.integration.manage',    'admin',     'Configure integrations and credentials',     true),
  ('admin.audit.read',            'admin',     'Read the audit log',                         true),
  ('admin.billing.manage',        'admin',     'Manage subscription and billing',            true),
  ('admin.apikey.manage',         'admin',     'Issue and revoke API keys',                  true)
on conflict (code) do update
  set module = excluded.module,
      description = excluded.description,
      is_sensitive = excluded.is_sensitive;

--------------------------------------------------------------------------------
-- System role templates (tenant_id is null). Copied into a tenant at
-- provisioning, after which the tenant may customise its own copies.
--------------------------------------------------------------------------------
insert into identity.role (id, tenant_id, code, name, description, is_system) values
  ('11111111-0000-0000-0000-000000000001', null, 'owner',          'Owner',
   'Full access to everything, including billing and audit.', true),
  ('11111111-0000-0000-0000-000000000002', null, 'sales_manager',  'Sales Manager',
   'Runs the sales floor: all leads and deals, gross visibility, deal approval.', true),
  ('11111111-0000-0000-0000-000000000003', null, 'salesperson',    'Salesperson',
   'Own leads and deals. No cost, gross, or finance-rate visibility by default.', true),
  ('11111111-0000-0000-0000-000000000004', null, 'finance_manager','F&I Manager',
   'Financing, products, contracting and signature.', true),
  ('11111111-0000-0000-0000-000000000005', null, 'office_manager', 'Office / Title Clerk',
   'Documents, titles, deal jackets, and paperwork completeness.', true),
  ('11111111-0000-0000-0000-000000000006', null, 'recon',          'Reconditioning',
   'Vehicle status, recon steps, photos, and costs.', true),
  ('11111111-0000-0000-0000-000000000007', null, 'read_only',      'Read Only',
   'View-only access with no sensitive data.', true)
on conflict (id) do nothing;

-- Owner: everything.
insert into identity.role_permission (role_id, permission_code)
select '11111111-0000-0000-0000-000000000001', code from identity.permission
on conflict do nothing;

insert into identity.role_permission (role_id, permission_code) values
  -- Sales Manager
  ('11111111-0000-0000-0000-000000000002','inventory.vehicle.read'),
  ('11111111-0000-0000-0000-000000000002','inventory.vehicle.write'),
  ('11111111-0000-0000-0000-000000000002','inventory.cost.read'),
  ('11111111-0000-0000-0000-000000000002','inventory.price.write'),
  ('11111111-0000-0000-0000-000000000002','inventory.photo.write'),
  ('11111111-0000-0000-0000-000000000002','inventory.publish'),
  ('11111111-0000-0000-0000-000000000002','crm.customer.read'),
  ('11111111-0000-0000-0000-000000000002','crm.customer.write'),
  ('11111111-0000-0000-0000-000000000002','crm.lead.read.all'),
  ('11111111-0000-0000-0000-000000000002','crm.lead.write'),
  ('11111111-0000-0000-0000-000000000002','crm.lead.assign'),
  ('11111111-0000-0000-0000-000000000002','crm.task.write'),
  ('11111111-0000-0000-0000-000000000002','crm.appointment.write'),
  ('11111111-0000-0000-0000-000000000002','sales.deal.read.all'),
  ('11111111-0000-0000-0000-000000000002','sales.deal.write'),
  ('11111111-0000-0000-0000-000000000002','sales.deal.approve'),
  ('11111111-0000-0000-0000-000000000002','sales.gross.read'),
  ('11111111-0000-0000-0000-000000000002','sales.commission.read.all'),
  ('11111111-0000-0000-0000-000000000002','documents.read'),
  ('11111111-0000-0000-0000-000000000002','documents.write'),
  ('11111111-0000-0000-0000-000000000002','signatures.send'),
  ('11111111-0000-0000-0000-000000000002','messaging.read'),
  ('11111111-0000-0000-0000-000000000002','messaging.send'),
  ('11111111-0000-0000-0000-000000000002','ai.generate'),
  ('11111111-0000-0000-0000-000000000002','ai.approve'),
  ('11111111-0000-0000-0000-000000000002','reports.read'),
  ('11111111-0000-0000-0000-000000000002','reports.financial.read'),
  ('11111111-0000-0000-0000-000000000002','reports.export'),

  -- Salesperson: deliberately WITHOUT inventory.cost.read, sales.gross.read,
  -- crm.customer.pii.read, or sales.deal.finance.read. This matches how most
  -- independent dealers actually operate; a tenant that disagrees can grant them.
  ('11111111-0000-0000-0000-000000000003','inventory.vehicle.read'),
  ('11111111-0000-0000-0000-000000000003','inventory.photo.write'),
  ('11111111-0000-0000-0000-000000000003','crm.customer.read'),
  ('11111111-0000-0000-0000-000000000003','crm.customer.write'),
  ('11111111-0000-0000-0000-000000000003','crm.lead.read'),
  ('11111111-0000-0000-0000-000000000003','crm.lead.write'),
  ('11111111-0000-0000-0000-000000000003','crm.task.write'),
  ('11111111-0000-0000-0000-000000000003','crm.appointment.write'),
  ('11111111-0000-0000-0000-000000000003','sales.deal.read'),
  ('11111111-0000-0000-0000-000000000003','sales.deal.write'),
  ('11111111-0000-0000-0000-000000000003','sales.commission.read'),
  ('11111111-0000-0000-0000-000000000003','documents.read'),
  ('11111111-0000-0000-0000-000000000003','documents.write'),
  ('11111111-0000-0000-0000-000000000003','messaging.read'),
  ('11111111-0000-0000-0000-000000000003','messaging.send'),
  ('11111111-0000-0000-0000-000000000003','ai.generate'),
  ('11111111-0000-0000-0000-000000000003','reports.read'),

  -- F&I Manager
  ('11111111-0000-0000-0000-000000000004','inventory.vehicle.read'),
  ('11111111-0000-0000-0000-000000000004','crm.customer.read'),
  ('11111111-0000-0000-0000-000000000004','crm.customer.write'),
  ('11111111-0000-0000-0000-000000000004','crm.customer.pii.read'),
  ('11111111-0000-0000-0000-000000000004','sales.deal.read.all'),
  ('11111111-0000-0000-0000-000000000004','sales.deal.write'),
  ('11111111-0000-0000-0000-000000000004','sales.deal.finance.read'),
  ('11111111-0000-0000-0000-000000000004','sales.deal.finance.write'),
  ('11111111-0000-0000-0000-000000000004','sales.gross.read'),
  ('11111111-0000-0000-0000-000000000004','documents.read'),
  ('11111111-0000-0000-0000-000000000004','documents.write'),
  ('11111111-0000-0000-0000-000000000004','signatures.send'),
  ('11111111-0000-0000-0000-000000000004','messaging.read'),
  ('11111111-0000-0000-0000-000000000004','messaging.send'),
  ('11111111-0000-0000-0000-000000000004','reports.read'),
  ('11111111-0000-0000-0000-000000000004','reports.financial.read'),

  -- Office / Title Clerk
  ('11111111-0000-0000-0000-000000000005','inventory.vehicle.read'),
  ('11111111-0000-0000-0000-000000000005','inventory.vehicle.write'),
  ('11111111-0000-0000-0000-000000000005','inventory.cost.read'),
  ('11111111-0000-0000-0000-000000000005','inventory.cost.write'),
  ('11111111-0000-0000-0000-000000000005','crm.customer.read'),
  ('11111111-0000-0000-0000-000000000005','crm.customer.write'),
  ('11111111-0000-0000-0000-000000000005','crm.customer.pii.read'),
  ('11111111-0000-0000-0000-000000000005','sales.deal.read.all'),
  ('11111111-0000-0000-0000-000000000005','documents.read'),
  ('11111111-0000-0000-0000-000000000005','documents.write'),
  ('11111111-0000-0000-0000-000000000005','documents.template.write'),
  ('11111111-0000-0000-0000-000000000005','signatures.send'),
  ('11111111-0000-0000-0000-000000000005','crm.task.write'),
  ('11111111-0000-0000-0000-000000000005','reports.read'),

  -- Reconditioning
  ('11111111-0000-0000-0000-000000000006','inventory.vehicle.read'),
  ('11111111-0000-0000-0000-000000000006','inventory.vehicle.write'),
  ('11111111-0000-0000-0000-000000000006','inventory.cost.read'),
  ('11111111-0000-0000-0000-000000000006','inventory.cost.write'),
  ('11111111-0000-0000-0000-000000000006','inventory.recon.write'),
  ('11111111-0000-0000-0000-000000000006','inventory.photo.write'),
  ('11111111-0000-0000-0000-000000000006','crm.task.write'),

  -- Read Only
  ('11111111-0000-0000-0000-000000000007','inventory.vehicle.read'),
  ('11111111-0000-0000-0000-000000000007','crm.customer.read'),
  ('11111111-0000-0000-0000-000000000007','crm.lead.read.all'),
  ('11111111-0000-0000-0000-000000000007','sales.deal.read.all'),
  ('11111111-0000-0000-0000-000000000007','documents.read'),
  ('11111111-0000-0000-0000-000000000007','reports.read')
on conflict do nothing;

--------------------------------------------------------------------------------
-- Plans
--------------------------------------------------------------------------------
insert into billing.plan (code, name, monthly_price, annual_price, max_users, max_vehicles,
                          max_ai_calls, max_ocr_pages, max_sms, max_storage_mb, features)
values
  ('starter',      'Starter',      149.00,  1490.00,  3,   75,   500,  200,  500,  10240,
   '{"marketplace_syndication": false, "esignature": true, "ocr": false, "api_access": false}'),
  ('professional', 'Professional', 349.00,  3490.00,  10,  300,  3000, 1500, 3000, 51200,
   '{"marketplace_syndication": true, "esignature": true, "ocr": true, "api_access": false}'),
  ('enterprise',   'Enterprise',   699.00,  6990.00,  40,  1500, 15000,7500, 15000,204800,
   '{"marketplace_syndication": true, "esignature": true, "ocr": true, "api_access": true}')
on conflict (code) do update
  set name = excluded.name,
      monthly_price = excluded.monthly_price,
      annual_price = excluded.annual_price,
      max_users = excluded.max_users,
      max_vehicles = excluded.max_vehicles,
      features = excluded.features;

--------------------------------------------------------------------------------
-- Launch-state jurisdictions
--
-- State-level rows only. County and city rows for Kansas (which assesses
-- destination-based local rates) are loaded from a sourced dataset in a
-- separate data migration, not hand-typed here.
--------------------------------------------------------------------------------
insert into sales.tax_jurisdiction (id, state_code, level, name) values
  ('22222222-0000-4000-8000-000000000040', 'OK', 'state', 'Oklahoma'),
  ('22222222-0000-4000-8000-000000000020', 'KS', 'state', 'Kansas'),
  ('22222222-0000-4000-8000-000000000048', 'TX', 'state', 'Texas')
on conflict (state_code, level, name, coalesce(fips_code, '')) do nothing;

--------------------------------------------------------------------------------
-- Rule-set SKELETONS.
--
-- approved_at IS NULL on purpose. sales.rule_set's uniqueness/overlap
-- constraint only applies to approved rows, and the deal engine loads only
-- approved rows, so these are inert until a reviewer fills in the values and
-- approves them. The structure below is the contract the DealEngine
-- deserializes; the values are placeholders.
--------------------------------------------------------------------------------
insert into sales.rule_set (jurisdiction_id, schema_version, version, effective_from,
                            rules, source_citation, notes)
select j.id, 1, 1, date '2026-01-01',
  jsonb_build_object(
    'taxBasis',            'UNVERIFIED',   -- how the taxable amount is derived
    'tradeInCreditPolicy', 'UNVERIFIED',   -- full | capped | none
    'stateRate',           null,           -- numeric(9,6)
    'localRateSource',     'UNVERIFIED',   -- none | destination | origin
    'rebateTaxable',       null,           -- boolean
    'docFeeCap',           null,           -- numeric or null when uncapped
    'docFeeTaxable',       null,
    'fees', jsonb_build_array(),           -- [{code,name,amount,taxable,stateFee}]
    'rounding',            'AwayFromZero',
    'roundingScale',       2
  ),
  'PLACEHOLDER — must cite the governing statute or DMV publication before approval',
  'Skeleton seeded by V0007. approved_at is NULL so the deal engine will refuse '
  'to price a deal in this jurisdiction. Populate from primary sources, have a '
  'CPA or dealer-compliance attorney review, then set approved_at/approved_by.'
from sales.tax_jurisdiction j
where j.level = 'state' and j.state_code in ('OK','KS','TX')
  and not exists (select 1 from sales.rule_set r where r.jurisdiction_id = j.id);
