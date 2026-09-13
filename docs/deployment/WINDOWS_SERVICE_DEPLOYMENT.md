# Windows Service Hosting & Deployment

## 1. Hosting Model

`RetailPOS.Server` is built with `Microsoft.Extensions.Hosting.WindowsServices` (`builder.Host.UseWindowsService()`). It can run either:
1. Interactively in console mode for development/debugging (`dotnet run`).
2. Headless as a background Windows Service for production installations (`sc.exe create`).

## 2. Directory Layout & Working Directory Independence

Windows Services default their current working directory to `C:\Windows\System32`.
Therefore, `RetailPOS.Server` **never assumes or relies on the current working directory**.

All runtime file paths are explicitly derived from `ServerOptions`:
* **Data Directory**: `%ProgramData%\GrozetPOS\Data` (authoritative `grozet_pos.db` SQLite database)
* **Logs Directory**: `%ProgramData%\GrozetPOS\Logs` (daily rolling log files `retailpos-server-yyyyMMdd.log`)
* **Backups Directory**: `%ProgramData%\GrozetPOS\Backups` (SQLite online VACUUM snapshots)

Environment variables are expanded deterministically via `Environment.ExpandEnvironmentVariables()`, and directories are ensured automatically upon startup.

## 3. Service Installation Commands

### Step A: Publish Release Binary
```cmd
dotnet publish src\RetailPOS.Server\RetailPOS.Server.csproj -c Release -r win-x64 --self-contained false -o "C:\Program Files\GrozetPOS\Server"
```

### Step B: Create Windows Service
```cmd
sc.exe create GrozetPOSServer binPath= "\"C:\Program Files\GrozetPOS\Server\RetailPOS.Server.exe\"" start= auto DisplayName= "Grozet Retail POS Server"
```

### Step C: Configure Automatic Recovery on Failure
```cmd
sc.exe failure GrozetPOSServer reset= 86400 actions= restart/60000/restart/60000/restart/60000
```

### Step D: Start the Service
```cmd
sc.exe start GrozetPOSServer
```

## 4. Verification

* **Service Status**: `sc.exe query GrozetPOSServer`
* **Health Check**: `curl -i http://localhost:5100/api/v1/health`
* **Database Check**: `curl -i http://localhost:5100/api/v1/health/database`
