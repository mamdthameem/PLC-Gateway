# How to Run PLC Gateway Application

## Quick Start (Recommended)

Open Command Prompt (CMD) or PowerShell and run:

```cmd
cd "d:\Project - ShotSense\PLCGateway\PLCGateway"
dotnet run
```

This will:
- Build the project automatically
- Run the application
- Show logs in the console

## Step-by-Step Method

### 1. Navigate to Project Directory
```cmd
cd "d:\Project - ShotSense\PLCGateway\PLCGateway"
```

### 2. Restore NuGet Packages (if needed)
```cmd
dotnet restore
```

### 3. Build the Project
```cmd
dotnet build
```

### 4. Run the Application
```cmd
dotnet run
```

## Build and Run Separately

### Build Only
```cmd
dotnet build
```

### Run from Build Output
```cmd
dotnet run --no-build
```

Or run the executable directly:
```cmd
.\bin\Debug\net10.0\PLCGateway.exe
```

## Build for Release

```cmd
dotnet build -c Release
dotnet run -c Release
```

## Check Application Status

When running, you should see:
- "Connected to PLC" message
- "Read {name} ({addr}) = {val}" messages
- Any error messages if PLC connection fails

## Stop the Application

Press `Ctrl+C` to stop the application gracefully.

## Troubleshooting

### If you get "dotnet command not found":
- Install .NET SDK 10.0 or later
- Download from: https://dotnet.microsoft.com/download

### If you get build errors:
- Make sure all files are saved
- Run `dotnet clean` then `dotnet build`

### If PLC connection fails:
- Check PLC IP address in `appsettings.json`
- Verify PLC is powered on and accessible
- Check network connectivity

### If database connection fails:
- Check PostgreSQL connection string in `appsettings.json`
- Verify PostgreSQL is running
- Check database exists
