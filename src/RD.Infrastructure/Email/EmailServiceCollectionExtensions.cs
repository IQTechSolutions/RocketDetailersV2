using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RD.Infrastructure.Email;

public static class EmailServiceCollectionExtensions
{
    /// <summary>
    /// Registers the transactional email sender: options (Email) plus the SMTP
    /// implementation. Registration is unconditional — an unconfigured relay is
    /// reported through <see cref="IEmailSender.IsConfigured"/>, not by leaving the
    /// service missing (a null service turns a configuration gap into a startup crash
    /// at the wrong layer).
    /// </summary>
    public static IServiceCollection AddRdEmail(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<EmailOptions>(config.GetSection(EmailOptions.SectionName));
        services.TryAddSingleton<IEmailSender, SmtpEmailSender>();
        return services;
    }
}
