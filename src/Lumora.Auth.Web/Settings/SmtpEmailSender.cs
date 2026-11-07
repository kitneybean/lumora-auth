using System.Net;
using Lumora.Auth.Core.Entities;
using Lumora.Auth.Data;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.AspNetCore.Identity;
using MimeKit;

namespace Lumora.Auth.Web.Settings;

/// <summary>Sends account e-mails through the SMTP server configured in the admin panel.</summary>
public sealed class SmtpEmailSender(SiteSettingsService settings, ILogger<SmtpEmailSender> logger) : IEmailSender<ApplicationUser>
{
    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendOrLogAsync(email, "Подтвердите e-mail", Layout(
            "Подтвердите e-mail",
            "Чтобы закончить регистрацию, подтвердите адрес почты.",
            confirmationLink, "Подтвердить e-mail"));

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendOrLogAsync(email, "Сброс пароля", Layout(
            "Сброс пароля",
            "Кто-то (надеемся, вы) запросил сброс пароля. Если это были не вы — просто проигнорируйте письмо.",
            resetLink, "Задать новый пароль"));

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendOrLogAsync(email, "Код для сброса пароля", Layout(
            "Сброс пароля", $"Код для сброса пароля: <b>{WebUtility.HtmlEncode(resetCode)}</b>", null, null));

    /// <summary>Sends a test e-mail with the given (possibly unsaved) settings; throws with a readable message on failure.</summary>
    public async Task SendTestAsync(SiteSettings s, string? password, string to, CancellationToken ct = default)
    {
        var name = WebUtility.HtmlEncode(s.ServerName);
        await SendAsync(s, password, to, $"Проверка почты {s.ServerName}",
            Layout("Почта работает", $"Это тестовое письмо от {name}. Письма о регистрации и сбросе пароля будут приходить так же.", null, null), ct);
    }

    private async Task SendOrLogAsync(string to, string subject, string html)
    {
        var s = settings.Current;
        if (!settings.MailConfigured(s))
        {
            logger.LogWarning("Mail is not configured: e-mail \"{Subject}\" to {To} was not sent", subject, to);
            return;
        }
        try
        {
            await SendAsync(s, settings.SmtpPassword(s), to, subject, html, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to send \"{Subject}\" to {To}", subject, to);
        }
    }

    private static async Task SendAsync(SiteSettings s, string? password, string to, string subject, string html, CancellationToken ct)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(s.MailFromName ?? s.ServerName, s.MailFromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = html, TextBody = StripTags(html) }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 20_000 };
        var security = s.SmtpSecurity switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            _ => SecureSocketOptions.None,
        };
        try
        {
            await client.ConnectAsync(s.SmtpHost, s.SmtpPort, security, ct);
            if (!string.IsNullOrEmpty(s.SmtpUsername))
                await client.AuthenticateAsync(s.SmtpUsername, password ?? "", ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);
        }
        catch (AuthenticationException e)
        {
            throw new InvalidOperationException("Сервер почты не принял логин или пароль. Для Яндекса, Mail.ru и Gmail нужен «пароль приложения», а не обычный пароль.", e);
        }
        catch (SslHandshakeException e)
        {
            throw new InvalidOperationException("Не удалось установить защищённое соединение — проверьте порт и тип шифрования (465 — SSL, 587 — STARTTLS).", e);
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException($"Не удалось подключиться к {s.SmtpHost}:{s.SmtpPort}. Проверьте адрес и порт.", e);
        }
    }

    private static string Layout(string title, string text, string? link, string? button)
    {
        var action = link is null ? "" :
            $"""
            <p style="margin:28px 0"><a href="{link}" style="background:#18a35e;color:#fff;text-decoration:none;padding:12px 22px;border-radius:8px;font-weight:600">{button}</a></p>
            <p style="color:#6b7280;font-size:13px">Если кнопка не работает, откройте ссылку:<br><a href="{link}" style="color:#18a35e;word-break:break-all">{link}</a></p>
            """;
        return $"""
            <div style="font-family:Segoe UI,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#14171c">
              <h2 style="margin:0 0 12px">{WebUtility.HtmlEncode(title)}</h2>
              <p style="font-size:15px;line-height:1.5">{text}</p>
              {action}
            </div>
            """;
    }

    private static string StripTags(string html) =>
        WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")).Trim();
}
