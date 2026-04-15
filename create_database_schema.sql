-- =====================================================
-- PLC Gateway Database Schema Creation Script
-- Two-Tier Architecture with COV Detection
-- =====================================================

-- Connect to your database first:
-- \c sreesakthi_gateway

-- =====================================================
-- TIER 1: Real-Time Current Values Table
-- Single row per tag - always holds the latest value
-- =====================================================

CREATE TABLE IF NOT EXISTS plc_current_values (
    address VARCHAR(255) PRIMARY KEY,
    parameter_name VARCHAR(255) NOT NULL,
    value TEXT,
    data_type VARCHAR(50) NOT NULL,
    last_updated TIMESTAMP DEFAULT NOW(),
    last_stored_historical TIMESTAMP,
    last_heartbeat TIMESTAMP
);

-- Index for fast lookups by parameter name
CREATE INDEX IF NOT EXISTS idx_current_values_parameter ON plc_current_values(parameter_name);
CREATE INDEX IF NOT EXISTS idx_current_values_updated ON plc_current_values(last_updated);

COMMENT ON TABLE plc_current_values IS 'Tier 1: Real-time current values - single row per PLC tag';
COMMENT ON COLUMN plc_current_values.address IS 'PLC address (e.g., DB1.DBW0) - Primary Key';
COMMENT ON COLUMN plc_current_values.last_stored_historical IS 'Timestamp when value was last written to historical table';
COMMENT ON COLUMN plc_current_values.last_heartbeat IS 'Timestamp when last periodic heartbeat was sent';

-- =====================================================
-- TIER 2: Historical Time-Series Data Table
-- Stores values based on COV, state change, or periodic heartbeat
-- =====================================================

CREATE TABLE IF NOT EXISTS plc_historical_data (
    id SERIAL PRIMARY KEY,
    address VARCHAR(255) NOT NULL,
    parameter_name VARCHAR(255) NOT NULL,
    value TEXT,
    data_type VARCHAR(50) NOT NULL,
    storage_reason VARCHAR(50) NOT NULL, -- 'COV', 'PERIODIC', 'STATE_CHANGE', 'VALUE_CHANGE'
    timestamp TIMESTAMP DEFAULT NOW(),
    previous_value TEXT
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_historical_address_timestamp ON plc_historical_data(address, timestamp);
CREATE INDEX IF NOT EXISTS idx_historical_timestamp ON plc_historical_data(timestamp);
CREATE INDEX IF NOT EXISTS idx_historical_address ON plc_historical_data(address);
CREATE INDEX IF NOT EXISTS idx_historical_parameter ON plc_historical_data(parameter_name);
CREATE INDEX IF NOT EXISTS idx_historical_storage_reason ON plc_historical_data(storage_reason);

COMMENT ON TABLE plc_historical_data IS 'Tier 2: Historical time-series data with COV + periodic storage';
COMMENT ON COLUMN plc_historical_data.storage_reason IS 'Reason for storage: COV (2% change), PERIODIC (60s heartbeat), STATE_CHANGE (bool), VALUE_CHANGE (string)';
COMMENT ON COLUMN plc_historical_data.previous_value IS 'Previous value for debugging and analysis';

-- =====================================================
-- CALCULATED METRICS TABLE
-- Stores only calculated parameters (not raw PLC values)
-- =====================================================

CREATE TABLE IF NOT EXISTS calculated_metrics (
    id SERIAL PRIMARY KEY,
    metric_name VARCHAR(255) NOT NULL,
    metric_value NUMERIC,
    period_type VARCHAR(50) NOT NULL, -- 'realtime', 'hour', 'shift', 'day', 'week', 'month', 'year', 'cumulative'
    period_value TIMESTAMP, -- Start time of the period (NULL for realtime/cumulative)
    calculation_timestamp TIMESTAMP DEFAULT NOW(),
    metadata JSONB -- Optional: stores additional context (e.g., chamber number, component info)
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_calculated_metric_name ON calculated_metrics(metric_name);
CREATE INDEX IF NOT EXISTS idx_calculated_period_type ON calculated_metrics(period_type);
CREATE INDEX IF NOT EXISTS idx_calculated_period_value ON calculated_metrics(period_value);
CREATE INDEX IF NOT EXISTS idx_calculated_metric_period ON calculated_metrics(metric_name, period_type, period_value);
CREATE INDEX IF NOT EXISTS idx_calculated_timestamp ON calculated_metrics(calculation_timestamp);

COMMENT ON TABLE calculated_metrics IS 'Stores only calculated parameters (not raw PLC values)';
COMMENT ON COLUMN calculated_metrics.period_type IS 'Time period: realtime, hour, shift, day, week, month, year, cumulative';
COMMENT ON COLUMN calculated_metrics.period_value IS 'Start timestamp of the period (NULL for realtime/cumulative)';
COMMENT ON COLUMN calculated_metrics.metadata IS 'JSON object for additional context (chamber number, component info, etc.)';

-- =====================================================
-- LONG-TERM AGGREGATION TABLE
-- Stores compressed hourly aggregations for old data
-- =====================================================

CREATE TABLE IF NOT EXISTS plc_aggregated_hourly (
    id SERIAL PRIMARY KEY,
    address VARCHAR(255) NOT NULL,
    parameter_name VARCHAR(255) NOT NULL,
    hour_timestamp TIMESTAMP NOT NULL, -- Start of the hour
    min_value NUMERIC,
    max_value NUMERIC,
    avg_value NUMERIC,
    sample_count INTEGER, -- Number of samples aggregated
    aggregation_date DATE DEFAULT CURRENT_DATE, -- When aggregation was performed
    data_age_days INTEGER NOT NULL -- Age of data when aggregated (30/60/90/365)
);

-- Indexes for performance
CREATE INDEX IF NOT EXISTS idx_aggregated_address_hour ON plc_aggregated_hourly(address, hour_timestamp);
CREATE INDEX IF NOT EXISTS idx_aggregated_hour_timestamp ON plc_aggregated_hourly(hour_timestamp);
CREATE INDEX IF NOT EXISTS idx_aggregated_address ON plc_aggregated_hourly(address);
CREATE INDEX IF NOT EXISTS idx_aggregated_parameter ON plc_aggregated_hourly(parameter_name);
CREATE INDEX IF NOT EXISTS idx_aggregated_age ON plc_aggregated_hourly(data_age_days);

COMMENT ON TABLE plc_aggregated_hourly IS 'Long-term aggregated data: min/max/avg per hour for data older than 30/60/90/365 days';
COMMENT ON COLUMN plc_aggregated_hourly.hour_timestamp IS 'Start timestamp of the hour (e.g., 2024-01-15 10:00:00)';
COMMENT ON COLUMN plc_aggregated_hourly.data_age_days IS 'Age of data when aggregated: 30, 60, 90, or 365 days';

-- =====================================================
-- VERIFICATION QUERIES
-- Run these to verify tables were created successfully
-- =====================================================

-- =====================================================
-- UNIQUE INDEXES for calculated_metrics upsert
--
-- WHY two indexes instead of one UNIQUE constraint:
-- PostgreSQL treats NULL != NULL in unique constraints,
-- so (metric_name, period_type, NULL) would never conflict.
-- Two partial indexes fix this:
--   1. Rows WITH a period_value  (hour/shift/day/week/month/year)
--   2. Rows WITHOUT period_value (realtime/cumulative)
-- =====================================================

CREATE UNIQUE INDEX IF NOT EXISTS uq_calculated_metrics_with_period
    ON calculated_metrics (metric_name, period_type, period_value)
    WHERE period_value IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_calculated_metrics_no_period
    ON calculated_metrics (metric_name, period_type)
    WHERE period_value IS NULL;

-- Check all tables exist
SELECT table_name 
FROM information_schema.tables 
WHERE table_schema = 'public' 
  AND table_name IN ('plc_current_values', 'plc_historical_data', 'calculated_metrics', 'plc_aggregated_hourly')
ORDER BY table_name;

-- View table structures
SELECT 
    table_name,
    column_name,
    data_type,
    is_nullable,
    column_default
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN ('plc_current_values', 'plc_historical_data', 'calculated_metrics', 'plc_aggregated_hourly')
ORDER BY table_name, ordinal_position;

-- Check indexes
SELECT 
    tablename,
    indexname,
    indexdef
FROM pg_indexes
WHERE schemaname = 'public'
  AND tablename IN ('plc_current_values', 'plc_historical_data', 'calculated_metrics', 'plc_aggregated_hourly')
ORDER BY tablename, indexname;
