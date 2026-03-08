using GentleSuite.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace GentleSuite.Infrastructure.Jobs;

public class BankSyncJob
{
    private readonly IIntegrationService _svc;
    private readonly ILogger<BankSyncJob> _log;

    public BankSyncJob(IIntegrationService svc, ILogger<BankSyncJob> log)
    { _svc = svc; _log = log; }

    public async Task SyncAllAsync()
    {
        _log.LogInformation("Bank-Sync-Job gestartet.");
        await _svc.SyncPayPalAsync();
        await _svc.SyncBankAsync();
        _log.LogInformation("Bank-Sync-Job abgeschlossen.");
    }
}
