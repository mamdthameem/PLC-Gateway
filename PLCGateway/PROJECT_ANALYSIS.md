# PLC Gateway Project - Architecture & Workflow Analysis

## 📋 Project Overview

This is a **PLC Gateway** application that acts as a bridge between a Siemens S7-1200 PLC and a PostgreSQL database. It continuously reads/writes PLC data, stores it in a two-tier database architecture, and performs data aggregation and calculations.

**Technology Stack:**
- .NET 10.0 (C#)
- S7.NetPlus library (Siemens PLC communication)
- PostgreSQL database
- Microsoft.Extensions.Hosting (Background services)

---

## 🏗️ Architecture Overview

### Two-Tier Data Storage Architecture

The project uses a **two-tier storage strategy** to optimize database performance:

1. **Tier 1 (Real-Time)**: `plc_current_values` table
   - Stores the latest value for each PLC tag
   - Always updated on every scan cycle
   - Fast lookups for real-time monitoring
   - Contains metadata: `last_updated`, `last_stored_historical`, `last_heartbeat`

2. **Tier 2 (Historical)**: `plc_historical_data` table
   - Stores time-series historical data
   - Only stores data when certain conditions are met (COV, state changes, periodic heartbeat)
   - Used for trend analysis and reporting

---

## 🔄 Core Components

### 1. **PlcService** (`PlcService.cs`)
- **Purpose**: Manages connection to Siemens S7-1200 PLC
- **Key Methods**:
  - `Connect()`: Establishes connection to PLC (idempotent)
  - `Read(address)`: Reads value from PLC address
  - `Write(address, value)`: Writes value to PLC address
- **Configuration**: IP address, rack, slot from `appsettings.json`

### 2. **DatabaseService** (`DatabaseService.cs`)
- **Purpose**: Handles all PostgreSQL database operations
- **Key Features**:
  - Retry logic with exponential backoff (5 retries max)
  - Two-tier storage methods:
    - `UpsertCurrentValueAsync()`: Updates Tier 1 (always)
    - `InsertHistoricalDataAsync()`: Inserts Tier 2 (conditional)
  - Calculated metrics storage

### 3. **CovDetectionService** (`CovDetectionService.cs`)
- **Purpose**: Determines when to store data in Tier 2 (historical)
- **Storage Rules**:
  - **Numeric values**: Store if:
    - COV (Change of Value) ≥ 2% deadband, OR
    - Periodic heartbeat (every 60 seconds)
  - **Boolean values**: Store only on state change
  - **String values**: Store only on value change
  - **Initial values**: Always stored (first time)

### 4. **GatewayWorker** (`GatewayWorker.cs`)
- **Purpose**: Main background service that orchestrates PLC communication
- **Workflow**:
  1. Loads tags from `appsettings.json`
  2. Connects to PLC (with retry logic)
  3. Scans all configured tags at interval (default: 1000ms)
  4. For each tag:
     - **Read Mode**: Reads from PLC → Updates Tier 1 → Conditionally stores in Tier 2
     - **Write Mode**: Writes to PLC from config or database
     - **ReadWrite Mode**: Does both

### 5. **AggregationService** (`AggregationService.cs`)
- **Purpose**: Calculates aggregated metrics at scheduled intervals
- **Schedule**:
  - **Hourly**: At top of each hour
  - **Shift**: Every 8 hours (configurable)
  - **Daily**: At midnight
  - **Weekly**: Monday at midnight
  - **Monthly**: 1st of month at midnight
  - **Yearly**: January 1st at midnight
- **Calls**: `CalculationService.CalculateAggregatedMetricsAsync()`

### 6. **DataAggregationService** (`DataAggregationService.cs`)
- **Purpose**: Long-term data compression (runs daily at 2 AM)
- **Function**: Aggregates old historical data (30, 60, 90, 365 days) into hourly min/max/avg
- **Process**:
  1. Aggregates numeric data older than threshold days
  2. Stores in `plc_aggregated_hourly` table
  3. Deletes original detailed records after aggregation

### 7. **CalculationService** (`CalculationService.cs`)
- **Purpose**: Performs business logic calculations
- **Status**: Currently placeholder - ready for implementation
- **Planned Metrics** (from config):
  - Machine Utility (%)
  - Production Quantity
  - Energy Consumption
  - Energy per Casting
  - And more...

---

## 🔄 Complete Workflow

### Startup Sequence (`Program.cs`)

```
1. Load configuration from appsettings.json
2. Register services:
   - PlcService (singleton)
   - DatabaseService (singleton)
   - CovDetectionService (singleton)
   - CalculationService (singleton)
3. Register background services:
   - GatewayWorker (main PLC communication)
   - AggregationService (metric calculations)
   - DataAggregationService (data compression)
4. Start host (runs as Windows Service)
```

### Main Execution Loop (GatewayWorker)

```
┌─────────────────────────────────────────────────┐
│  GatewayWorker Loop (every 1000ms)              │
└─────────────────────────────────────────────────┘
                    │
                    ▼
        ┌───────────────────────┐
        │ Connect to PLC        │
        │ (with retry logic)    │
        └───────────────────────┘
                    │
                    ▼
        ┌───────────────────────┐
        │ For each Tag:         │
        └───────────────────────┘
                    │
        ┌───────────┴───────────┐
        │                       │
        ▼                       ▼
┌───────────────┐      ┌───────────────┐
│ READ Mode     │      │ WRITE Mode    │
└───────────────┘      └───────────────┘
        │                       │
        ▼                       ▼
┌───────────────────────────────────────┐
│ 1. Read value from PLC                │
│ 2. Upsert to Tier 1 (always)          │
│ 3. Check COV/State/Heartbeat          │
│ 4. If condition met → Store Tier 2    │
└───────────────────────────────────────┘
```

### Data Storage Decision Flow

```
Read Value from PLC
        │
        ▼
Update Tier 1 (plc_current_values) ← Always
        │
        ▼
CovDetectionService.ShouldStoreInHistoricalAsync()
        │
        ├─→ INITIAL? → Store in Tier 2
        ├─→ COV (≥2%)? → Store in Tier 2
        ├─→ STATE_CHANGE (BOOL)? → Store in Tier 2
        ├─→ VALUE_CHANGE (STRING)? → Store in Tier 2
        ├─→ PERIODIC (60s)? → Store in Tier 2
        └─→ None → Skip Tier 2
```

### Background Services Timeline

```
GatewayWorker:        [████████████████████] Continuous (1s interval)
AggregationService:    [─┼──┼──┼──┼──┼──┼──] Hourly/Shift/Daily/Weekly/Monthly
DataAggregationService:[──────┼────────────] Daily at 2 AM
```

---

## ⚙️ Configuration (`appsettings.json`)

### PLC Connection
```json
"PLC": {
  "IpAddress": "192.168.0.180",
  "Rack": 0,
  "Slot": 1
}
```

### Database Connection
```json
"PostgreSQL": {
  "ConnectionString": "Host=localhost;Port=5432;Database=sreesakthi_gateway;Username=postgres;Password=Pass"
}
```

### Scan Interval
```json
"ScanIntervalMs": 1000  // Milliseconds between PLC scans
```

### Data Collection Rules
```json
"DataCollection": {
  "CovDeadbandPercent": 2.0,        // 2% change threshold
  "PeriodicHeartbeatSeconds": 60      // Heartbeat interval
}
```

### Tags Configuration
Each tag has:
- `Name`: Human-readable name
- `Address`: PLC address (e.g., "DB60.DBB0")
- `DataType`: INT, REAL, BOOL, STRING, DINT, etc.
- `Mode`: "read", "write", or "readwrite"
- `WriteValue`: (Optional) Default value for write operations

---

## 🚀 How to Run the Project

### Prerequisites

1. **PLC Connection**:
   - Siemens S7-1200 PLC must be powered on
   - PLC must be accessible on network
   - IP address must match configuration

2. **Database Setup**:
   - PostgreSQL must be running
   - Database must exist (e.g., `sreesakthi_gateway`)
   - Required tables must be created:
     - `plc_current_values` (Tier 1)
     - `plc_historical_data` (Tier 2)
     - `plc_aggregated_hourly` (aggregated data)
     - `calculated_metrics` (calculated metrics)

3. **Configuration**:
   - Update `appsettings.json` with correct:
     - PLC IP address, rack, slot
     - PostgreSQL connection string
     - Tag addresses and names

### Running Steps

#### Option 1: Run as Console Application (Development)

```bash
# Navigate to project directory
cd "D:\Project - ShotSense\PLCGateway\PLCGateway"

# Restore packages (if needed)
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run
```

#### Option 2: Run as Windows Service (Production)

```bash
# Build the project
dotnet build -c Release

# Publish the application
dotnet publish -c Release -o ./publish

# Install as Windows Service (requires admin)
sc create PLCGatewayService binPath="C:\path\to\publish\PLCGateway.exe"
sc start PLCGatewayService
```

### Verification Steps

1. **Check Logs**:
   - Application logs to console
   - Look for: "Connected to PLC" message
   - Check for any connection errors

2. **Verify PLC Connection**:
   - Logs should show: "Read {name} ({addr}) = {val}"
   - If connection fails, logs show retry attempts

3. **Verify Database**:
   - Check `plc_current_values` table for updated values
   - Check `plc_historical_data` table for historical records
   - Verify `last_updated` timestamps are recent

4. **Monitor Background Services**:
   - GatewayWorker: Should show continuous read/write operations
   - AggregationService: Should trigger at scheduled times
   - DataAggregationService: Should run daily at 2 AM

---

## 🔍 Key Features

### 1. **Intelligent Data Storage**
- Only stores historical data when meaningful changes occur
- Reduces database size while preserving important data points
- Periodic heartbeats ensure data continuity

### 2. **Resilient Connection Handling**
- Automatic PLC reconnection on failure
- Database retry logic with exponential backoff
- Graceful error handling

### 3. **Bidirectional Communication**
- Read: PLC → Database
- Write: Database/Config → PLC
- ReadWrite: Both directions

### 4. **Data Compression**
- Long-term data aggregated to hourly summaries
- Original detailed data deleted after aggregation
- Configurable retention periods (30, 60, 90, 365 days)

### 5. **Scalable Architecture**
- Background services run independently
- Singleton services for shared resources
- Configurable scan intervals and thresholds

---

## 📊 Database Schema (Expected)

### Tier 1: `plc_current_values`
- `address` (PK): PLC address
- `parameter_name`: Tag name
- `value`: Current value (string)
- `data_type`: Data type
- `last_updated`: Last update timestamp
- `last_stored_historical`: Last Tier 2 storage time
- `last_heartbeat`: Last periodic heartbeat time

### Tier 2: `plc_historical_data`
- `id` (PK): Auto-increment
- `address`: PLC address
- `parameter_name`: Tag name
- `value`: Historical value
- `data_type`: Data type
- `storage_reason`: INITIAL, COV, STATE_CHANGE, VALUE_CHANGE, PERIODIC
- `timestamp`: When value was stored
- `previous_value`: Previous value (for comparison)

### Aggregated: `plc_aggregated_hourly`
- Hourly min/max/avg for old data
- Used for long-term trend analysis

---

## ⚠️ Important Notes

1. **PLC Connection**: The application will continuously retry if PLC is not available. Ensure PLC is accessible before starting.

2. **Database Tables**: Make sure all required tables exist before running. The application does not create tables automatically.

3. **Tag Configuration**: All PLC tags must be correctly configured in `appsettings.json`. Invalid addresses will cause read/write failures.

4. **Write Operations**: Write values come from:
   - `WriteValue` in tag config (priority 1)
   - Last value from database (priority 2)
   - Only writes if value changed (optimization)

5. **Windows Service**: The application is configured to run as a Windows Service (`UseWindowsService()`), making it suitable for production deployment.

---

## 🐛 Troubleshooting

### PLC Connection Issues
- Verify IP address, rack, and slot in config
- Check network connectivity to PLC
- Ensure PLC is in RUN mode
- Check firewall settings

### Database Connection Issues
- Verify connection string
- Check PostgreSQL is running
- Verify database exists
- Check user permissions

### No Data in Historical Table
- Check COV threshold (default 2%)
- Verify periodic heartbeat interval (default 60s)
- Check if values are actually changing
- Review logs for storage reasons

---

## 📝 Summary

This PLC Gateway is a robust industrial data acquisition system that:
- ✅ Connects to Siemens S7-1200 PLC
- ✅ Stores data in two-tier architecture (real-time + historical)
- ✅ Uses intelligent storage rules (COV, state changes, heartbeats)
- ✅ Performs scheduled aggregations and calculations
- ✅ Compresses long-term data for efficiency
- ✅ Supports bidirectional communication (read/write)
- ✅ Runs as Windows Service for production use

The system is designed for continuous operation in industrial environments with automatic recovery from connection failures.
