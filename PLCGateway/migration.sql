-- PLCGateway schema migration
-- Idempotent — safe to run on a fresh database or on top of v1.
-- Drops obsolete tables, creates all required tables, adds new columns to existing ones.

DROP TABLE IF EXISTS calculated_metrics;
DROP TABLE IF EXISTS plc_aggregated_hourly;

-- ─────────────────────────────────────────────────────────────────────────────
-- TIER 1: Real-time current values — one row per tag, always latest
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_current_values (
    address                 VARCHAR(50)  PRIMARY KEY,
    parameter_name          VARCHAR(200) NOT NULL,
    value                   TEXT,
    data_type               VARCHAR(20)  NOT NULL,
    last_updated            TIMESTAMP    DEFAULT NOW(),
    last_stored_historical  TIMESTAMP,
    last_heartbeat          TIMESTAMP
);

-- ─────────────────────────────────────────────────────────────────────────────
-- TIER 2: Historical time-series — COV-based, never deleted
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_historical_data (
    id              SERIAL       PRIMARY KEY,
    address         VARCHAR(50)  NOT NULL,
    parameter_name  VARCHAR(200) NOT NULL,
    value           TEXT,
    data_type       VARCHAR(20)  NOT NULL,
    storage_reason  VARCHAR(30)  NOT NULL,
    timestamp       TIMESTAMP    NOT NULL DEFAULT NOW(),
    previous_value  TEXT
);

CREATE INDEX IF NOT EXISTS idx_historical_name_time
    ON plc_historical_data (parameter_name, timestamp);

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION 1: Lifetime cumulative parameters — one row per parameter
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_lifetime_parameters (
    parameter_name  VARCHAR(100) PRIMARY KEY,
    value           NUMERIC,
    updated_at      TIMESTAMP DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- CYCLE LOG: one row per completed blast cycle
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_cycles (
    cycle_number        SERIAL    PRIMARY KEY,
    blast_start         TIMESTAMP NOT NULL,
    blast_end           TIMESTAMP NOT NULL,
    duration_sec        NUMERIC   NOT NULL,
    metal_1_name        TEXT,
    metal_1_weight_kg   NUMERIC,
    metal_2_name        TEXT,
    metal_2_weight_kg   NUMERIC,
    metal_3_name        TEXT,
    metal_3_weight_kg   NUMERIC,
    metal_4_name        TEXT,
    metal_4_weight_kg   NUMERIC,
    tonnage_kg          NUMERIC,
    recorded_at         TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_cycles_blast_start ON plc_cycles (blast_start);
CREATE INDEX IF NOT EXISTS idx_cycles_metal_1     ON plc_cycles (metal_1_name);
CREATE INDEX IF NOT EXISTS idx_cycles_metal_2     ON plc_cycles (metal_2_name);
CREATE INDEX IF NOT EXISTS idx_cycles_metal_3     ON plc_cycles (metal_3_name);
CREATE INDEX IF NOT EXISTS idx_cycles_metal_4     ON plc_cycles (metal_4_name);

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION 2: Dashboard trigger — insert here to request a filtered calculation
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS calculation_requests (
    id                SERIAL      PRIMARY KEY,
    filter_start      TIMESTAMP   NOT NULL,
    filter_end        TIMESTAMP   NOT NULL,
    period_label      VARCHAR(50),
    filter_by         VARCHAR(20) NOT NULL DEFAULT 'time',  -- 'time' | 'cycle' | 'metal'
    filter_cycle_from INTEGER,
    filter_cycle_to   INTEGER,
    filter_metal_name TEXT,
    status            VARCHAR(20) NOT NULL DEFAULT 'pending',
    created_at        TIMESTAMP   DEFAULT NOW(),
    processed_at      TIMESTAMP
);

-- Add new columns if upgrading from v1 (safe to run again — IF NOT EXISTS)
ALTER TABLE calculation_requests ADD COLUMN IF NOT EXISTS filter_by         VARCHAR(20) NOT NULL DEFAULT 'time';
ALTER TABLE calculation_requests ADD COLUMN IF NOT EXISTS filter_cycle_from INTEGER;
ALTER TABLE calculation_requests ADD COLUMN IF NOT EXISTS filter_cycle_to   INTEGER;
ALTER TABLE calculation_requests ADD COLUMN IF NOT EXISTS filter_metal_name TEXT;

CREATE INDEX IF NOT EXISTS idx_calc_requests_pending
    ON calculation_requests (created_at)
    WHERE status = 'pending';

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION 2: Aggregate results — one row per parameter per request
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_filtered_parameters (
    id              SERIAL   PRIMARY KEY,
    request_id      INTEGER  NOT NULL REFERENCES calculation_requests(id),
    parameter_name  VARCHAR(100) NOT NULL,
    value           NUMERIC,
    calculated_at   TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_filtered_params_request
    ON plc_filtered_parameters (request_id);

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION 2: Per-cycle breakdown — one row per cycle per request
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_filtered_cycle_data (
    id                  SERIAL    PRIMARY KEY,
    request_id          INTEGER   NOT NULL REFERENCES calculation_requests(id),
    cycle_number        INTEGER   NOT NULL,
    blast_start         TIMESTAMP,
    blast_end           TIMESTAMP,
    metal_1_name        TEXT,
    metal_1_weight_kg   NUMERIC,
    metal_2_name        TEXT,
    metal_2_weight_kg   NUMERIC,
    metal_3_name        TEXT,
    metal_3_weight_kg   NUMERIC,
    metal_4_name        TEXT,
    metal_4_weight_kg   NUMERIC,
    production_kg       NUMERIC,
    energy_kwh          NUMERIC,
    shots_usage         NUMERIC,
    calculated_at       TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_filtered_cycle_request
    ON plc_filtered_cycle_data (request_id);

-- ─────────────────────────────────────────────────────────────────────────────
-- SHOTS BREAKDOWN (Section 1): one row per refill interval, cleared and rewritten each minute
-- Shared dataset for parameters #7 (shots usage) and #8 (refill time) — dashboard renders as table/graph
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_shots_breakdown (
    id                  SERIAL    PRIMARY KEY,
    refill_timestamp    TIMESTAMP NOT NULL,
    blast_count         INTEGER   NOT NULL,
    calculated_at       TIMESTAMP DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- SHOTS BREAKDOWN (Section 2): one row per refill interval per calculation request
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_filtered_shots_breakdown (
    id                  SERIAL    PRIMARY KEY,
    request_id          INTEGER   NOT NULL REFERENCES calculation_requests(id),
    refill_timestamp    TIMESTAMP NOT NULL,
    blast_count         INTEGER   NOT NULL,
    calculated_at       TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_filtered_shots_request
    ON plc_filtered_shots_breakdown (request_id);

-- Rename energy_amp_sec → energy_kwh in plc_filtered_cycle_data (idempotent)
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_name = 'plc_filtered_cycle_data' AND column_name = 'energy_amp_sec'
    ) THEN
        ALTER TABLE plc_filtered_cycle_data RENAME COLUMN energy_amp_sec TO energy_kwh;
    END IF;
END $$;

-- ─────────────────────────────────────────────────────────────────────────────
-- TYPED VALUE COLUMNS (Part C1): numeric / boolean / text alongside the original TEXT
-- `value`. The old `value` column is KEPT FROZEN — never dropped, left NULL on new rows —
-- so historical raw text is preserved while calculations move to SQL-side typed aggregation.
-- Backfills are guarded (WHERE ... IS NULL) so the migration is idempotent and re-runnable.
-- ─────────────────────────────────────────────────────────────────────────────
ALTER TABLE plc_current_values  ADD COLUMN IF NOT EXISTS value_num  NUMERIC;
ALTER TABLE plc_current_values  ADD COLUMN IF NOT EXISTS value_bool BOOLEAN;
ALTER TABLE plc_current_values  ADD COLUMN IF NOT EXISTS value_text TEXT;
ALTER TABLE plc_historical_data ADD COLUMN IF NOT EXISTS value_num  NUMERIC;
ALTER TABLE plc_historical_data ADD COLUMN IF NOT EXISTS value_bool BOOLEAN;
ALTER TABLE plc_historical_data ADD COLUMN IF NOT EXISTS value_text TEXT;

-- Backfill Tier 1
UPDATE plc_current_values SET value_bool = (lower(value) IN ('1','true'))
    WHERE upper(data_type) IN ('BOOL','BOOLEAN') AND value_bool IS NULL AND value IS NOT NULL;
UPDATE plc_current_values SET value_text = value
    WHERE upper(data_type) IN ('STRING','CHAR','VARCHAR') AND value_text IS NULL AND value IS NOT NULL;
UPDATE plc_current_values SET value_num = trim(value)::numeric
    WHERE upper(data_type) NOT IN ('BOOL','BOOLEAN','STRING','CHAR','VARCHAR')
      AND value_num IS NULL AND value IS NOT NULL
      AND trim(value) ~ '^-?[0-9]+([.][0-9]+)?([eE][-+]?[0-9]+)?$';

-- Backfill Tier 2 (must run before the cycle energy backfill below, which reads value_num)
UPDATE plc_historical_data SET value_bool = (lower(value) IN ('1','true'))
    WHERE upper(data_type) IN ('BOOL','BOOLEAN') AND value_bool IS NULL AND value IS NOT NULL;
UPDATE plc_historical_data SET value_text = value
    WHERE upper(data_type) IN ('STRING','CHAR','VARCHAR') AND value_text IS NULL AND value IS NOT NULL;
UPDATE plc_historical_data SET value_num = trim(value)::numeric
    WHERE upper(data_type) NOT IN ('BOOL','BOOLEAN','STRING','CHAR','VARCHAR')
      AND value_num IS NULL AND value IS NOT NULL
      AND trim(value) ~ '^-?[0-9]+([.][0-9]+)?([eE][-+]?[0-9]+)?$';

CREATE INDEX IF NOT EXISTS idx_historical_name_num
    ON plc_historical_data (parameter_name, timestamp) INCLUDE (value_num);

-- ─────────────────────────────────────────────────────────────────────────────
-- PER-CYCLE PRODUCTION + ENERGY (Part C2): stored once at cycle close so windowed
-- production can be broken down by casting metal and lifetime energy is a simple SUM
-- (no per-cycle amp re-query every aggregation pass).
-- ─────────────────────────────────────────────────────────────────────────────
ALTER TABLE plc_cycles ADD COLUMN IF NOT EXISTS production_kg NUMERIC;
ALTER TABLE plc_cycles ADD COLUMN IF NOT EXISTS energy_kwh    NUMERIC;

-- production_kg backfill: accumulated tonnage delta vs the previous cycle, floored at 0.
WITH d AS (
    SELECT cycle_number,
           GREATEST(COALESCE(tonnage_kg, 0) - COALESCE(LAG(tonnage_kg) OVER (ORDER BY cycle_number), 0), 0) AS prod
    FROM plc_cycles
)
UPDATE plc_cycles c SET production_kg = d.prod
FROM d WHERE c.cycle_number = d.cycle_number AND c.production_kg IS NULL;

-- energy_kwh backfill: Σ over the 10 impellers of (avg amps in window) × duration hours.
-- Per impeller, use the mean of in-window readings; when a cycle had no in-window reading
-- for an impeller, fall back to that impeller's last reading before the cycle (matching the
-- original per-cycle energy calculation exactly), else 0.
WITH imps AS (
    SELECT 'Current_imp_' || g AS pname FROM generate_series(1, 10) g
),
e AS (
    SELECT c.cycle_number,
           SUM(
               COALESCE(
                   (SELECT AVG(h.value_num) FROM plc_historical_data h
                     WHERE h.parameter_name = i.pname
                       AND h.timestamp > c.blast_start AND h.timestamp <= c.blast_end
                       AND h.value_num IS NOT NULL),
                   (SELECT h2.value_num FROM plc_historical_data h2
                     WHERE h2.parameter_name = i.pname
                       AND h2.timestamp <= c.blast_start
                       AND h2.value_num IS NOT NULL
                     ORDER BY h2.timestamp DESC LIMIT 1),
                   0
               )
           ) * (c.duration_sec / 3600.0) AS energy
    FROM plc_cycles c
    CROSS JOIN imps i
    GROUP BY c.cycle_number, c.duration_sec
)
UPDATE plc_cycles c SET energy_kwh = ROUND(e.energy, 6)
FROM e WHERE c.cycle_number = e.cycle_number AND c.energy_kwh IS NULL;

-- Re-round any previously-backfilled high-scale energy values (idempotent).
UPDATE plc_cycles SET energy_kwh = ROUND(energy_kwh, 6)
WHERE energy_kwh IS NOT NULL AND scale(energy_kwh) > 6;

-- ─────────────────────────────────────────────────────────────────────────────
-- INCREMENTAL AGGREGATION STATE (Part D1): single row holding the watermark and running
-- accumulators for Section 1, so each pass processes only Tier 2 rows newer than the
-- watermark instead of replaying the full lifetime every minute.
-- Reset this row (or run the app with --rebuild-aggregation) to replay from scratch.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_aggregation_state (
    id                      INTEGER PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    last_hist_id            BIGINT            NOT NULL DEFAULT 0,
    -- Blast ON/OFF on-time + rising-edge count
    blast_seeded            BOOLEAN           NOT NULL DEFAULT FALSE,
    blast_on                BOOLEAN           NOT NULL DEFAULT FALSE,
    blast_seg_start         TIMESTAMP,
    blast_closed_sec        DOUBLE PRECISION  NOT NULL DEFAULT 0,
    first_blast_ts          TIMESTAMP,
    cycle_count             BIGINT            NOT NULL DEFAULT 0,
    -- Machine status on-time (denominator clamped to start at first_blast_ts, as before)
    machine_seeded          BOOLEAN           NOT NULL DEFAULT FALSE,
    machine_on              BOOLEAN           NOT NULL DEFAULT FALSE,
    machine_seg_start       TIMESTAMP,
    machine_closed_sec      DOUBLE PRECISION  NOT NULL DEFAULT 0,
    -- Refill tracking (refill weight change events)
    refill_count            BIGINT            NOT NULL DEFAULT 0,
    first_refill_change_ts  TIMESTAMP,
    prev_refill_change_ts   TIMESTAMP,
    last_refill_any_ts      TIMESTAMP,
    -- Energy running total (sum of plc_cycles.energy_kwh)
    energy_total            NUMERIC           NOT NULL DEFAULT 0,
    last_cycle_number       INTEGER           NOT NULL DEFAULT 0
);

INSERT INTO plc_aggregation_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ─────────────────────────────────────────────────────────────────────────────
-- SECTION 2 PER-METAL PRODUCTION (Part D3): one row per casting metal per request,
-- production split proportionally by declared metal weight within each cycle.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_filtered_metal_production (
    id             SERIAL   PRIMARY KEY,
    request_id     INTEGER  NOT NULL REFERENCES calculation_requests(id),
    metal_name     TEXT     NOT NULL,
    production_kg  NUMERIC  NOT NULL,
    calculated_at  TIMESTAMP DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_filtered_metal_request
    ON plc_filtered_metal_production (request_id);

-- ─────────────────────────────────────────────────────────────────────────────
-- SHOTS BREAKDOWN race fix (Part C3): unique key on refill_timestamp so the table is
-- maintained by idempotent upsert instead of TRUNCATE+rewrite (no empty-read window).
-- ─────────────────────────────────────────────────────────────────────────────
CREATE UNIQUE INDEX IF NOT EXISTS idx_shots_breakdown_refill
    ON plc_shots_breakdown (refill_timestamp);

-- ─────────────────────────────────────────────────────────────────────────────
-- GATEWAY STATUS: single row — PLC connection state, written by GatewayWorker.
-- Dashboard and admin API read this to show "PLC disconnected".
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS gateway_status (
    id             INTEGER  PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    plc_connected  BOOLEAN  NOT NULL DEFAULT FALSE,
    changed_at     TIMESTAMP,
    last_scan_at   TIMESTAMP
);

INSERT INTO gateway_status (id, plc_connected)
VALUES (1, FALSE)
ON CONFLICT (id) DO NOTHING;

-- Tier 1 staleness flag: TRUE while the PLC is disconnected (last-known values are
-- not live). Cleared per row as fresh scans overwrite values.
ALTER TABLE plc_current_values ADD COLUMN IF NOT EXISTS is_stale BOOLEAN NOT NULL DEFAULT FALSE;

-- ─────────────────────────────────────────────────────────────────────────────
-- DASHBOARD USERS (Part E): local login accounts (no tenant/subscription concept).
-- Seeded by the app on first run; passwords are SHA-256 hex. Replaces EF EnsureCreated.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS users (
    id               SERIAL       PRIMARY KEY,
    username         VARCHAR(100) UNIQUE NOT NULL,
    email            VARCHAR(255) UNIQUE NOT NULL,
    full_name        VARCHAR(150) NOT NULL DEFAULT '',
    password_hash    VARCHAR(128) NOT NULL,
    role             VARCHAR(30)  NOT NULL DEFAULT 'user',
    is_approved      BOOLEAN      NOT NULL DEFAULT TRUE,
    valid_until_utc  TIMESTAMP,
    created_at_utc   TIMESTAMP    NOT NULL DEFAULT NOW()
);

-- ─────────────────────────────────────────────────────────────────────────────
-- LICENSE STATE (Part E5): single row tracking the last successful cloud license check.
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS gateway_license_state (
    id                INTEGER   PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    last_success_utc  TIMESTAMP,
    last_check_utc    TIMESTAMP,
    locked            BOOLEAN   NOT NULL DEFAULT FALSE
);

INSERT INTO gateway_license_state (id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- ─────────────────────────────────────────────────────────────────────────────
-- SPARE STATUS: 10 impellers × 14 spares = 140 rows (upserted by SpareMonitoringService)
-- ─────────────────────────────────────────────────────────────────────────────
CREATE TABLE IF NOT EXISTS plc_spare_status (
    impeller_num        INTEGER NOT NULL,
    spare_index         INTEGER NOT NULL,
    spare_name          TEXT    NOT NULL,
    threshold_hours     NUMERIC NOT NULL,
    current_run_hours   NUMERIC DEFAULT 0,
    trigger_active      BOOLEAN DEFAULT FALSE,
    last_replaced_at    TIMESTAMP,
    last_updated_at     TIMESTAMP DEFAULT NOW(),
    PRIMARY KEY (impeller_num, spare_index)
);
