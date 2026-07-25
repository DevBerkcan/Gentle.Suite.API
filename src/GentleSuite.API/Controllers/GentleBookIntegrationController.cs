using GentleSuite.Application.DTOs;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GentleSuite.API.Controllers;

/// <summary>
/// Server-to-server integration surface for GentleBook (separate SaaS product by the same
/// operator). Auth is handled entirely by GentleBookApiKeyMiddleware, not ASP.NET Identity —
/// this controller is intentionally AllowAnonymous.
/// </summary>
[ApiController]
[Route("api/integrations/gentlebook")]
[AllowAnonymous]
public class GentleBookIntegrationController(
    ICustomerService customerSvc,
    IInvoiceService invoiceSvc,
    AppDbContext db) : ControllerBase
{
    [HttpPost("subscription-payments")]
    public async Task<IActionResult> RecordSubscriptionPayment(
        GentleBookSubscriptionPaymentRequest req, CancellationToken ct)
    {
        // Idempotency first — a retried push for a payment already invoiced is a no-op.
        var existing = await db.Invoices
            .FirstOrDefaultAsync(i => i.ExternalPaymentReference == req.MolliePaymentId, ct);
        if (existing != null)
        {
            return Ok(new GentleBookSubscriptionPaymentResponse(
                existing.CustomerId.ToString(), existing.Id, existing.InvoiceNumber));
        }

        var externalRef = $"GB-{req.GentleBookTenantId}";
        var customer = await db.Customers
            .FirstOrDefaultAsync(c => c.ExternalRef == externalRef, ct);

        Guid customerId;
        if (customer != null)
        {
            customerId = customer.Id;
        }
        else
        {
            var created = await customerSvc.CreateAsync(new CreateCustomerRequest(
                CompanyName: req.TenantLegalName,
                Industry: "GentleBook-Tenant",
                Website: null,
                TaxId: null,
                VatId: req.TenantVatId,
                PrimaryContact: new CreateContactRequest(
                    FirstName: req.TenantLegalName,
                    LastName: "",
                    Email: req.ContactEmail,
                    Phone: null,
                    Position: null,
                    IsPrimary: true),
                PrimaryLocation: new CreateLocationRequest(
                    Label: "Rechnungsadresse",
                    Street: req.BillingStreet,
                    City: req.BillingCity,
                    ZipCode: req.BillingZip,
                    Country: req.BillingCountry,
                    IsPrimary: true),
                DesiredServiceIds: null), ct);

            customerId = created.Id;

            // CreateCustomerRequest has no ExternalRef slot — set it directly, this is the
            // one place we bypass the service layer for a field the DTO doesn't expose.
            await db.Customers.Where(c => c.Id == customerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.ExternalRef, externalRef)
                    .SetProperty(c => c.Status, CustomerStatus.Active), ct);
        }

        var invoiceDto = await invoiceSvc.CreateAsync(new CreateInvoiceRequest(
            CustomerId: customerId,
            QuoteId: null,
            Subject: $"GentleBook Abonnement – {req.PlanName}",
            IntroText: null,
            OutroText: null,
            Notes: $"Automatisch erzeugt aus GentleBook-Zahlung {req.MolliePaymentId}.",
            TaxMode: TaxMode.SmallBusiness,
            PaymentTermDays: 0,
            Lines: new List<CreateInvoiceLineRequest>
            {
                new(
                    Title: $"GentleBook Abonnement – {req.PlanName}",
                    Description: $"Abrechnungszeitraum {req.BillingPeriodStart:dd.MM.yyyy} – {req.BillingPeriodEnd:dd.MM.yyyy}",
                    Unit: "Monat",
                    Quantity: 1,
                    UnitPrice: req.Amount,
                    VatPercent: 0)
            }), ct);

        // CreateInvoiceRequest has no BillingPeriod/ExternalPaymentReference slots either —
        // set them directly on the still-Draft invoice before finalizing.
        await db.Invoices.Where(i => i.Id == invoiceDto.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.BillingPeriodStart, req.BillingPeriodStart)
                .SetProperty(i => i.BillingPeriodEnd, req.BillingPeriodEnd)
                .SetProperty(i => i.ExternalPaymentReference, req.MolliePaymentId)
                .SetProperty(i => i.SmallBusinessNote, "Gemäß § 19 UStG wird keine Umsatzsteuer berechnet."), ct);

        await invoiceSvc.FinalizeAsync(invoiceDto.Id, new FinalizeInvoiceRequest(SendEmail: true), ct);

        var finalDto = await invoiceSvc.RecordPaymentAsync(invoiceDto.Id, new RecordPaymentRequest(
            Amount: req.Amount,
            PaymentDate: req.PaymentDate,
            PaymentMethod: "Mollie SEPA",
            Reference: req.MolliePaymentId,
            Note: null), ct);

        return Ok(new GentleBookSubscriptionPaymentResponse(
            customerId.ToString(), finalDto.Id, finalDto.InvoiceNumber));
    }
}

public record GentleBookSubscriptionPaymentRequest(
    Guid GentleBookTenantId,
    string TenantLegalName,
    string? TenantVatId,
    string BillingStreet,
    string BillingZip,
    string BillingCity,
    string BillingCountry,
    string ContactEmail,
    string PlanName,
    decimal Amount,
    DateTimeOffset BillingPeriodStart,
    DateTimeOffset BillingPeriodEnd,
    DateTimeOffset PaymentDate,
    string MolliePaymentId
);

public record GentleBookSubscriptionPaymentResponse(
    string CrmCustomerId,
    Guid InvoiceId,
    string InvoiceNumber
);
