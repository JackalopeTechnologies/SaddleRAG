// MongoEndPointText.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

#region Usings

using System.Net;

#endregion

namespace SaddleRAG.Database;

/// <summary>
///     Renders a MongoDB server endpoint as <c>host:port</c>. The driver's own
///     <see cref="DnsEndPoint" /> text carries an address-family prefix
///     ("Unspecified/localhost:27017") that means nothing to a reader.
/// </summary>
internal static class MongoEndPointText
{
    public static string Format(EndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        string text = endPoint switch
            {
                DnsEndPoint dns => $"{dns.Host}:{dns.Port}",
                var _ => endPoint.ToString() ?? string.Empty
            };
        return text;
    }

    /// <summary>
    ///     Joins several endpoints into one readable list.
    /// </summary>
    public const string Separator = ", ";
}
