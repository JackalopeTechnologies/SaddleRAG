// DatabaseFailure.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using MongoDB.Driver;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Decides whether a failure means SaddleRAG's database could not be reached
///     and, when it does, expresses it as a <see cref="DatabaseUnavailableException" />
///     that names the endpoint — so every caller reports an outage the same way.
/// </summary>
public static class DatabaseFailure
{
    /// <summary>
    ///     Returns the failure as a <see cref="DatabaseUnavailableException" />, or null
    ///     when it is not a database outage.
    ///     <para>
    ///         A driver connection failure is an outage (a rejected login is not). The
    ///         driver's server-selection timeout carries no endpoint and can come from
    ///         other causes, so a timeout counts only while a database is known to be
    ///         unreachable — <paramref name="unreachableEndpoints" />, read from the
    ///         driver's own state. Cancellation never counts, even when a timeout caused it.
    ///     </para>
    /// </summary>
    public static DatabaseUnavailableException? AsUnavailable(Exception exception,
                                                              IReadOnlyCollection<string> unreachableEndpoints)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(unreachableEndpoints);

        IEnumerable<Exception> candidates = exception is AggregateException aggregate
                                                ? aggregate.Flatten().InnerExceptions
                                                : [exception];
        var result = candidates.Select(candidate => Classify(candidate, unreachableEndpoints))
                               .FirstOrDefault(unavailable => unavailable != null);
        return result;
    }

    private static DatabaseUnavailableException? Classify(Exception candidate,
                                                          IReadOnlyCollection<string> unreachableEndpoints) =>
        candidate switch
            {
                DatabaseUnavailableException unavailable => unavailable,
                MongoAuthenticationException => null,
                MongoConnectionException connection =>
                    new DatabaseUnavailableException(MongoEndPointText.Format(connection.ConnectionId.ServerId.EndPoint),
                                                     connection
                                                    ),
                TimeoutException timeout when unreachableEndpoints.Count > 0 =>
                    new DatabaseUnavailableException(string.Join(MongoEndPointText.Separator, unreachableEndpoints),
                                                     timeout
                                                    ),
                var _ => null
            };
}
