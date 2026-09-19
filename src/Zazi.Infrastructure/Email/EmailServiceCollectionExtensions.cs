using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Zazi.Application.Email;

namespace Zazi.Infrastructure.Email;

/// <summary>
/// Wires up email for a host.
/// </summary>
/// <remarks>
/// One place, called by both the API and the dashboard. The portal has already been caught once
/// starting without a service the API had registered, so the registration lives here rather than
/// being written out twice and drifting.
/// </remarks>
public static class EmailServiceCollectionExtensions
{
    /// <summary>Resend's API root. Not configurable: there is one, and it is this.</summary>
    private const string ResendBaseAddress = "https://api.resend.com/";

    /// <summary>
    /// Registers <see cref="IEmailSender"/> according to configuration.
    /// </summary>
    /// <param name="isDevelopment">
    /// Whether this host is running in Development. It decides what an unconfigured deployment
    /// gets: a sender that writes messages to the log, or one that refuses and says so.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// When a deployment asks for a real provider and cannot use one. Configuration that says
    /// "send email" but cannot is worth failing a deployment over — the alternative is a signup
    /// page that appears to work while nobody receives anything.
    /// </exception>
    public static IServiceCollection AddZaziEmail(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        var options = new EmailOptions();
        configuration.GetSection(EmailOptions.SectionName).Bind(options);
        options.Validate();

        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));

        // Read once here, from the environment only, and handed to the sender as its own object.
        // Nothing else in the container can reach it.
        var credential = ResendCredential.FromEnvironment();
        services.AddSingleton(credential);

        if (options.Provider == EmailProvider.Resend)
        {
            if (!credential.IsPresent)
            {
                throw new InvalidOperationException(
                    $"{EmailOptions.SectionName}:Provider is set to Resend but the "
                    + $"{EmailOptions.ApiKeyEnvironmentVariable} environment variable is empty. "
                    + "Set it in the service's environment file (never in appsettings.json, which "
                    + "is committed). Zazi will not start with email configured but unusable.");
            }

            services.AddHttpClient<IEmailSender, ResendEmailSender>(
                ResendEmailSender.HttpClientName,
                client =>
                {
                    client.BaseAddress = new Uri(ResendBaseAddress);
                    client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
                    client.DefaultRequestHeaders.Accept.Add(
                        new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
                });

            return services;
        }

        // No provider. What that means depends on where this is running, and the difference
        // matters: LoggingEmailSender writes the message body — which carries one-time
        // verification links — into the log.
        if (isDevelopment)
        {
            services.AddSingleton<IEmailSender, LoggingEmailSender>();
        }
        else
        {
            services.AddSingleton<IEmailSender, DisabledEmailSender>();
        }

        return services;
    }
}
