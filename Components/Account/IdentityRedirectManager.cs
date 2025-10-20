using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace VaxSync.Web.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(5),
    };

    [DoesNotReturn]
    public void RedirectTo(string? uri)
    {
        var destination = NormalizeDestination(uri);

        // During static rendering, NavigateTo throws a NavigationException which is handled by the framework as a redirect.
        // So as long as this is called from a statically rendered Identity component, the InvalidOperationException is never thrown.
        navigationManager.NavigateTo(destination, forceLoad: true);
        throw new InvalidOperationException($"{nameof(IdentityRedirectManager)} can only be used during static rendering.");
    }

    [DoesNotReturn]
    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        RedirectTo(newUri);
    }

    [DoesNotReturn]
    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    private string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    [DoesNotReturn]
    public void RedirectToCurrentPage() => RedirectTo(CurrentPath);

    [DoesNotReturn]
    public void RedirectToCurrentPageWithStatus(string message, HttpContext context)
        => RedirectToWithStatus(CurrentPath, message, context);

    private string NormalizeDestination(string? uri)
    {
        var destination = uri?.Trim();

        if (string.IsNullOrEmpty(destination))
        {
            return "/";
        }

        // Prevent open redirects by forcing everything to this application's base URI.
        if (!Uri.IsWellFormedUriString(destination, UriKind.Relative))
        {
            destination = navigationManager.ToBaseRelativePath(destination);
        }

        if (string.IsNullOrEmpty(destination) || destination == "?")
        {
            return "/";
        }

        if (destination.StartsWith("//", StringComparison.Ordinal))
        {
            destination = destination.TrimStart('/');
        }

        if (!destination.StartsWith('/', StringComparison.Ordinal) &&
            !destination.StartsWith('?', StringComparison.Ordinal))
        {
            destination = $"/{destination}";
        }

        return destination;
    }
}
