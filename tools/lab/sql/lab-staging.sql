-- Wardsage test lab (staging ONLY). Apply as supabase_admin with --single-transaction to the STAGING db container,
-- then: NOTIFY pgrst, 'reload schema'.  Never apply to prod.  Idempotent (safe to re-run).
--
-- Model: one row + token per device.  A device (the runner on the owner's test laptop) talks to PostgREST with a JWT
-- (role=lab_device, sub=device id, gen=token generation).  It has NO table access: only the four SECURITY DEFINER RPCs
-- lab_heartbeat / lab_claim / lab_finish / lab_put_file, which check the device, its generation, enabled flag and the
-- global kill switch, and rate-limit the calls.  Jobs are queued by the server side (service role / supabase_admin).

-- ------------------------------------------------------------------ role
do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'lab_device') then
    create role lab_device nologin noinherit;
  end if;
end $$;
grant lab_device to authenticator;
grant usage on schema public to lab_device;
alter role lab_device set statement_timeout = '8s';

-- ------------------------------------------------------------------ tables
create table if not exists public.lab_settings (
  key   text primary key check (key ~ '^[a-z_]{1,40}$'),
  value text not null check (char_length(value) <= 200)
);
insert into public.lab_settings (key, value) values ('kill_switch', 'off') on conflict do nothing;

create table if not exists public.lab_devices (
  id         uuid primary key default gen_random_uuid(),
  name       text not null unique check (name ~ '^[a-z0-9-]{1,32}$'),
  account_id uuid references public.game_accounts (id) on delete set null,   -- the game account the test app syncs as
  enabled    boolean not null default true,
  token_gen  integer not null default 1,
  last_seen  timestamptz,
  status     jsonb not null default '{}'::jsonb check (octet_length(status::text) <= 4096),
  created_at timestamptz not null default now()
);

create table if not exists public.lab_runs (
  id          uuid primary key default gen_random_uuid(),
  device_id   uuid not null references public.lab_devices (id) on delete cascade,
  scenarios   text[] not null,
  build       text not null default 'a' check (build in ('a', 'b')),
  state       text not null default 'queued' check (state in ('queued', 'running', 'done', 'failed', 'timeout', 'cancelled')),
  phase       text not null default 'start',
  data        jsonb not null default '{}'::jsonb,       -- state machine memory (job ids, timestamps)
  steps       jsonb not null default '[]'::jsonb,       -- [{name, ok, seconds, note}]
  created_at  timestamptz not null default now(),
  started_at  timestamptz,
  finished_at timestamptz
);
create index if not exists lab_runs_device_idx on public.lab_runs (device_id, created_at desc);

create table if not exists public.lab_jobs (
  id          uuid primary key default gen_random_uuid(),
  device_id   uuid not null references public.lab_devices (id) on delete cascade,
  run_id      uuid references public.lab_runs (id) on delete set null,
  action      text not null check (action in ('status', 'update_app', 'start_game', 'restart_app', 'screenshot', 'upload_logs', 'finish', 'cancel')),
  args        jsonb not null default '{}'::jsonb check (jsonb_typeof(args) = 'object' and octet_length(args::text) < 2048),
  state       text not null default 'queued' check (state in ('queued', 'claimed', 'done', 'failed', 'expired', 'cancelled')),
  created_at  timestamptz not null default now(),
  expires_at  timestamptz not null default now() + interval '3 hours',
  claimed_at  timestamptz,
  finished_at timestamptz,
  result      jsonb check (result is null or octet_length(result::text) <= 8192)
);
-- existing installs: the action list grew (start_game)
alter table public.lab_jobs drop constraint if exists lab_jobs_action_check;
alter table public.lab_jobs add constraint lab_jobs_action_check
  check (action in ('status', 'update_app', 'start_game', 'restart_app', 'screenshot', 'upload_logs', 'finish', 'cancel'));
create index if not exists lab_jobs_device_state_idx on public.lab_jobs (device_id, state, created_at);

create table if not exists public.lab_results (
  id        bigserial primary key,
  job_id    uuid not null references public.lab_jobs (id) on delete cascade,
  device_id uuid not null references public.lab_devices (id) on delete cascade,
  at        timestamptz not null default now(),
  ok        boolean not null,
  summary   jsonb check (summary is null or octet_length(summary::text) <= 8192)
);

create table if not exists public.lab_files (
  id         uuid primary key default gen_random_uuid(),
  device_id  uuid not null references public.lab_devices (id) on delete cascade,
  job_id     uuid references public.lab_jobs (id) on delete set null,
  name       text not null check (name ~ '^[A-Za-z0-9._-]{1,64}$'),
  mime       text not null check (mime in ('image/jpeg', 'image/png', 'text/plain')),
  size       integer not null check (size between 1 and 1572864),
  data       bytea not null check (octet_length(data) <= 1572864),
  created_at timestamptz not null default now()
);
create index if not exists lab_files_device_idx on public.lab_files (device_id, created_at desc);

create table if not exists public.lab_fixtures (
  id          uuid primary key default gen_random_uuid(),
  device_id   uuid not null references public.lab_devices (id) on delete cascade,
  name        text not null check (name in ('equip_a', 'equip_b', 'locate')),
  payload     jsonb not null check (octet_length(payload::text) <= 65536),
  captured_at timestamptz not null default now(),
  unique (device_id, name)
);

create table if not exists public.lab_rate (
  device_id uuid not null,
  kind      text not null,
  bucket    timestamptz not null,
  n         integer not null default 0,
  primary key (device_id, kind, bucket)
);

-- RLS on, no policies: nobody but the service role / owner reads or writes these tables directly.
do $$
declare t text;
begin
  foreach t in array array['lab_settings', 'lab_devices', 'lab_runs', 'lab_jobs', 'lab_results', 'lab_files', 'lab_fixtures', 'lab_rate'] loop
    execute format('alter table public.%I enable row level security', t);
    execute format('revoke all on public.%I from public, anon, authenticated, lab_device', t);
  end loop;
end $$;
revoke all on sequence public.lab_results_id_seq from public, anon, authenticated, lab_device;

-- ------------------------------------------------------------------ internals (not callable by the device)
-- The calling device: checks claims, device row, enabled, generation, kill switch; then the rate limit.
create or replace function public.lab_dev(p_kind text, p_limit integer)
returns uuid
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  c jsonb;
  d public.lab_devices;
  b timestamptz := date_trunc('minute', now());
  cnt integer;
begin
  begin
    c := nullif(current_setting('request.jwt.claims', true), '')::jsonb;
  exception when others then
    c := null;
  end;
  if c is null or c->>'role' <> 'lab_device' then
    raise exception 'lab_forbidden' using errcode = 'PT403';
  end if;
  if coalesce((select value from public.lab_settings where key = 'kill_switch'), 'off') <> 'off' then
    raise exception 'lab_killed' using errcode = 'PT403';
  end if;
  begin
    select * into d from public.lab_devices where id = (c->>'sub')::uuid;
  exception when others then
    raise exception 'lab_unknown_device' using errcode = 'PT403';
  end;
  if not found then raise exception 'lab_unknown_device' using errcode = 'PT403'; end if;
  if not d.enabled then raise exception 'lab_disabled' using errcode = 'PT403'; end if;
  if (c->>'gen') is null or (c->>'gen') !~ '^[0-9]{1,9}$' or (c->>'gen')::integer <> d.token_gen then
    raise exception 'lab_bad_gen' using errcode = 'PT403';
  end if;
  insert into public.lab_rate as r (device_id, kind, bucket, n) values (d.id, p_kind, b, 1)
    on conflict (device_id, kind, bucket) do update set n = r.n + 1
    returning r.n into cnt;
  if cnt > p_limit then raise exception 'lab_rate' using errcode = 'PT429'; end if;
  delete from public.lab_rate where bucket < now() - interval '1 hour' and random() < 0.05;
  return d.id;
end $$;
revoke all on function public.lab_dev(text, integer) from public, anon, authenticated, lab_device;

-- ------------------------------------------------------------------ device RPCs
create or replace function public.lab_heartbeat(p_status jsonb default '{}'::jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare d uuid := public.lab_dev('hb', 12);
begin
  if p_status is null or jsonb_typeof(p_status) <> 'object' or octet_length(p_status::text) > 4096 then
    raise exception 'lab_bad_status' using errcode = 'PT400';
  end if;
  update public.lab_devices set last_seen = now(), status = p_status where id = d;
  return jsonb_build_object('ok', true, 'now', now());
end $$;

create or replace function public.lab_claim()
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  d uuid := public.lab_dev('claim', 12);
  j public.lab_jobs;
begin
  update public.lab_jobs set state = 'expired', finished_at = now()
    where device_id = d and state in ('queued', 'claimed') and expires_at < now();
  -- one job at a time: a job claimed less than 15 minutes ago is still running
  if exists (select 1 from public.lab_jobs where device_id = d and state = 'claimed' and claimed_at > now() - interval '15 minutes') then
    return null;
  end if;
  update public.lab_jobs set state = 'failed', finished_at = now(), result = '{"error":"abandoned"}'::jsonb
    where device_id = d and state = 'claimed';
  select * into j from public.lab_jobs
    where device_id = d and state = 'queued' and expires_at >= now()
    order by created_at limit 1 for update skip locked;
  if not found then return null; end if;
  update public.lab_jobs set state = 'claimed', claimed_at = now() where id = j.id;
  if j.action = 'cancel' then
    update public.lab_jobs set state = 'cancelled', finished_at = now() where device_id = d and state = 'queued' and id <> j.id;
  end if;
  return jsonb_build_object('id', j.id, 'action', j.action, 'args', j.args, 'expires_at', j.expires_at);
end $$;

create or replace function public.lab_finish(p_job uuid, p_ok boolean, p_result jsonb default '{}'::jsonb)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  d uuid := public.lab_dev('finish', 30);
  n integer;
begin
  if p_result is null or octet_length(p_result::text) > 8192 then
    raise exception 'lab_bad_result' using errcode = 'PT400';
  end if;
  update public.lab_jobs set state = case when p_ok then 'done' else 'failed' end, finished_at = now(), result = p_result
    where id = p_job and device_id = d and state = 'claimed';
  get diagnostics n = row_count;
  if n = 0 then raise exception 'lab_no_such_job' using errcode = 'PT404'; end if;
  insert into public.lab_results (job_id, device_id, ok, summary) values (p_job, d, p_ok, p_result);
  return jsonb_build_object('ok', true);
end $$;

-- p_b64: standard base64 of the file; 1..1.5 MB decoded; the job must be claimed by this device
create or replace function public.lab_put_file(p_job uuid, p_name text, p_mime text, p_b64 text)
returns jsonb
language plpgsql
security definer
set search_path = public, pg_temp
as $$
declare
  d uuid := public.lab_dev('file', 6);
  raw bytea;
  fid uuid;
begin
  if p_name is null or p_name !~ '^[A-Za-z0-9._-]{1,64}$' or p_mime not in ('image/jpeg', 'image/png', 'text/plain') then
    raise exception 'lab_bad_file' using errcode = 'PT400';
  end if;
  if p_b64 is null or length(p_b64) > 2200000 then
    raise exception 'lab_file_too_big' using errcode = 'PT413';
  end if;
  if not exists (select 1 from public.lab_jobs where id = p_job and device_id = d and state = 'claimed') then
    raise exception 'lab_no_such_job' using errcode = 'PT404';
  end if;
  raw := decode(p_b64, 'base64');
  if octet_length(raw) < 1 or octet_length(raw) > 1572864 then
    raise exception 'lab_file_too_big' using errcode = 'PT413';
  end if;
  insert into public.lab_files (device_id, job_id, name, mime, size, data)
    values (d, p_job, p_name, p_mime, octet_length(raw), raw) returning id into fid;
  delete from public.lab_files where device_id = d and created_at < now() - interval '14 days';
  return jsonb_build_object('ok', true, 'size', octet_length(raw));
end $$;

revoke all on function public.lab_heartbeat(jsonb) from public, anon, authenticated;
revoke all on function public.lab_claim() from public, anon, authenticated;
revoke all on function public.lab_finish(uuid, boolean, jsonb) from public, anon, authenticated;
revoke all on function public.lab_put_file(uuid, text, text, text) from public, anon, authenticated;
grant execute on function public.lab_heartbeat(jsonb) to lab_device;
grant execute on function public.lab_claim() to lab_device;
grant execute on function public.lab_finish(uuid, boolean, jsonb) to lab_device;
grant execute on function public.lab_put_file(uuid, text, text, text) to lab_device;

notify pgrst, 'reload schema';
