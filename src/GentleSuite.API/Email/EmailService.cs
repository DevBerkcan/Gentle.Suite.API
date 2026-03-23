using Fluid;
using GentleSuite.Application.Interfaces;
using GentleSuite.Domain.Entities;
using GentleSuite.Domain.Enums;
using GentleSuite.Infrastructure.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace GentleSuite.Infrastructure.Email;

public class SmtpSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 465;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string FromEmail { get; set; } = "noreply@gentlesuite.local";
    public string FromName { get; set; } = "GentleSuite";
    public bool UseSsl { get; set; } = true;
}

public class EmailServiceImpl : IEmailService
{
    private readonly AppDbContext _db;
    private readonly SmtpSettings _smtp;
    private readonly ILogger<EmailServiceImpl> _log;
    private static readonly FluidParser _parser = new();

    public EmailServiceImpl(AppDbContext db, IOptions<SmtpSettings> smtp, ILogger<EmailServiceImpl> log)
    {
        _db = db;
        _smtp = smtp.Value;
        _log = log;
    }

    private SecureSocketOptions GetSocketOptions()
    {
        if (_smtp.Port == 465) return SecureSocketOptions.SslOnConnect;
        if (_smtp.UseSsl) return SecureSocketOptions.StartTls;
        return SecureSocketOptions.None;
    }

    private async Task<SmtpClient> CreateConnectedClientAsync(CancellationToken ct)
    {
        var client = new SmtpClient();
        client.Timeout = 20000;
        await client.ConnectAsync(_smtp.Host, _smtp.Port, GetSocketOptions(), ct);
        if (!string.IsNullOrEmpty(_smtp.Username))
            await client.AuthenticateAsync(_smtp.Username, _smtp.Password, ct);
        return client;
    }

    public async Task SendEmailAsync(string to, string subject, string body, string? cc = null, List<string>? attachments = null, CancellationToken ct = default)
    {
        var log = new EmailLog { To = to, Subject = subject, Body = body, Status = EmailStatus.Sending, Cc = cc };
        _db.EmailLogs.Add(log);
        await _db.SaveChangesAsync(ct);
        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(_smtp.FromName, _smtp.FromEmail));
            msg.To.Add(MailboxAddress.Parse(to));
            if (!string.IsNullOrWhiteSpace(cc)) msg.Cc.Add(MailboxAddress.Parse(cc));
            msg.Subject = subject;
            msg.Body = new BodyBuilder { HtmlBody = body }.ToMessageBody();

            using var client = await CreateConnectedClientAsync(ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);

            log.Status = EmailStatus.Sent;
            log.SentAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            log.Status = EmailStatus.Failed;
            log.Error = ex.Message;
            _log.LogError(ex, "Email failed to {To}", to);
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task SendTemplatedEmailAsync(string to, string templateKey, Dictionary<string, object> variables, Guid? customerId = null, List<string>? attachments = null, CancellationToken ct = default)
    {
        var tmpl = await _db.EmailTemplates.FirstOrDefaultAsync(t => t.Key == templateKey && t.IsActive, ct);
        if (tmpl == null)
        {
            _log.LogWarning("Email template '{Key}' not found or inactive", templateKey);
            return;
        }

        var ctx = new TemplateContext();
        foreach (var kv in variables) ctx.SetValue(kv.Key, kv.Value);

        var subject = await _parser.Parse(tmpl.Subject).RenderAsync(ctx);
        var body = await _parser.Parse(tmpl.Body).RenderAsync(ctx);

        var log = new EmailLog
        {
            To = to,
            Subject = subject,
            Body = body,
            Status = EmailStatus.Sending,
            TemplateKey = templateKey,
            CustomerId = customerId
        };
        _db.EmailLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        try
        {
            var msg = new MimeMessage();
            msg.From.Add(new MailboxAddress(_smtp.FromName, _smtp.FromEmail));
            msg.To.Add(MailboxAddress.Parse(to));
            msg.Subject = subject;
            msg.Body = new BodyBuilder { HtmlBody = body }.ToMessageBody();

            using var client = await CreateConnectedClientAsync(ct);
            await client.SendAsync(msg, ct);
            await client.DisconnectAsync(true, ct);

            log.Status = EmailStatus.Sent;
            log.SentAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            log.Status = EmailStatus.Failed;
            log.Error = ex.Message;
            _log.LogError(ex, "Templated email '{Key}' failed to {To}", templateKey, to);
        }
        await _db.SaveChangesAsync(ct);
    }
}
