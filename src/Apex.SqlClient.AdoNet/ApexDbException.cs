using System.Data.Common;

namespace Apex.SqlClient;

/// <summary>
/// An ADO.NET error wrapping the original native Apex exception in <see cref="Exception.InnerException"/>.
/// </summary>
public sealed class ApexDbException : DbException
{
    internal ApexDbException(
        SqlClientException exception,
        string? sqlState = null,
        bool isTransient = false,
        int? errorCode = null)
        : base(exception.Message, exception)
    {
        SqlState = sqlState;
        IsTransient = isTransient;
        HResult = errorCode ?? exception.HResult;
    }

    /// <summary>Gets the SQLSTATE supplied by the provider, or null when unavailable.</summary>
    public override string? SqlState { get; }

    /// <summary>Gets whether the native provider classifies the error as transient.</summary>
    public override bool IsTransient { get; }
}

internal static class ApexExceptionBoundary
{
    internal static T Invoke<T>(Func<T> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            return operation();
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async Task RunAsync(Func<Task> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async Task<T> RunAsync<T>(Func<Task<T>> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async ValueTask RunValueAsync(Func<ValueTask> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }

    internal static async ValueTask<T> RunValueAsync<T>(Func<ValueTask<T>> operation, Func<SqlClientException, DbException> translate)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (SqlClientException exception)
        {
            throw translate(exception);
        }
    }
}
