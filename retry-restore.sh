#!/usr/bin/env bash
# retry-restore.sh — Tente un dotnet restore toutes les 2 min jusqu'au succès
# Lance en background: nohup bash retry-restore.sh &

set -euo pipefail

PROJ="/home/sevan/Documents/Projects/ME/AtmLogAgent/src/AtmLogAgent.Service/AtmLogAgent.Service.csproj"
LOG="/home/sevan/Documents/Projects/ME/AtmLogAgent/restore.log"
MAX_ATTEMPTS=60

echo "[$(date)] Starting restore retry loop (max ${MAX_ATTEMPTS} attempts)..." | tee "$LOG"

for i in $(seq 1 $MAX_ATTEMPTS); do
    echo "[$(date)] Attempt $i/$MAX_ATTEMPTS..." | tee -a "$LOG"
    
    # Quick connectivity test
    if ! curl -4 -s --max-time 8 -o /dev/null https://api.nuget.org/v3/index.json; then
        echo "[$(date)] NuGet unreachable — waiting 2 min..." | tee -a "$LOG"
        sleep 120
        continue
    fi
    
    echo "[$(date)] NuGet reachable — attempting restore..." | tee -a "$LOG"
    if dotnet restore "$PROJ" --verbosity minimal 2>&1 | tee -a "$LOG"; then
        echo "[$(date)] ✅ RESTORE SUCCESS!" | tee -a "$LOG"
        
        # Build immediately
        echo "[$(date)] Building..." | tee -a "$LOG"
        if dotnet build "$PROJ" -c Release --no-restore 2>&1 | tee -a "$LOG"; then
            echo "[$(date)] ✅ BUILD SUCCESS!" | tee -a "$LOG"
            
            # Publish
            dotnet publish "$PROJ" -c Release --no-restore -o /home/sevan/Documents/Projects/ME/AtmLogAgent/publish 2>&1 | tee -a "$LOG"
            echo "[$(date)] ✅ PUBLISH COMPLETE — Ready for Docker!" | tee -a "$LOG"
        fi
        exit 0
    else
        echo "[$(date)] Restore failed — retrying in 2 min..." | tee -a "$LOG"
        sleep 120
    fi
done

echo "[$(date)] ❌ All attempts exhausted." | tee -a "$LOG"
exit 1
