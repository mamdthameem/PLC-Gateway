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
