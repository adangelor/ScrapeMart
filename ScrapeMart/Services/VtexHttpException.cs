// File: Services/VtexHttpException.cs
using System.Net;

namespace ScrapeMart.Services;

public sealed class VtexHttpException : Exception
{
    public System.Net.HttpStatusCode StatusCode { get; }
    public string RawBody { get; }
    public object? Context { get; }

    public VtexHttpException(string message, System.Net.HttpStatusCode statusCode, string rawBody, object? context)
        : base($"{message}. Status: {statusCode}. Body: {rawBody}")
    {
        StatusCode = statusCode;
        RawBody = rawBody;
        Context = context;
    }

    public static VtexHttpException FromResponse(string context, HttpStatusCode status, string? body)
    {
        var msg = $"VTEX {context} failed with {(int)status} {status}. Body: {Truncate(body, 240)}";
        return new VtexHttpException(context, status, body, msg);
    }

    private static string Truncate(string? s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");
}
