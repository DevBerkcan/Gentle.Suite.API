# Dashboard checks

Run arithmetic checks:

```powershell
dotnet run --project tests/Dashboard.Checks/Dashboard.Checks.csproj
```

On Windows with SQL Server LocalDB, run the actual overview aggregation against a disposable database:

```powershell
dotnet run --project tests/Dashboard.Checks/Dashboard.Checks.csproj -- --integration
```

The integration check creates a unique `GentleSuiteDashboardTest_*` database and removes only that database in `finally`. It never reads application connection strings. Optional `--fixture=path.json` writes the aggregated populated and empty responses for local UI tests.

Coverage: empty data, monthly recurring contract prices (including quarterly billing), missing prices, separation of installments, partial and future payments, chargebacks, imported paid invoices without payment rows, unpaid fully invoiced installment plans, overdue invoice statuses, scheduled collections, and missing billing authorization.
