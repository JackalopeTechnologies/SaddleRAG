// BsonDocumentExtensions.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.

#region Usings

using MongoDB.Bson;

#endregion

namespace SaddleRAG.Database.Migrations;

/// <summary>
///     Defensive accessors used by the one-time legacy-jobs migration.
///     Each helper returns the requested type when the field is present
///     and the right BSON kind, otherwise the supplied default; missing
///     fields and shape mismatches never throw, which is critical when
///     reading documents whose authoring schema predates the current
///     codebase.
/// </summary>
internal static class BsonDocumentExtensions
{
    internal static string GetValueOrString(this BsonDocument doc, string name, string fallback = "")
    {
        string result = fallback;
        if (doc.TryGetValue(name, out BsonValue value) && !value.IsBsonNull)
            result = ConvertToString(value, fallback);
        return result;
    }

    internal static string? GetValueOrNullableString(this BsonDocument doc, string name)
    {
        string? result = null;
        if (doc.TryGetValue(name, out BsonValue value) && !value.IsBsonNull)
            result = value.IsString ? value.AsString : value.ToString();
        return result;
    }

    internal static int GetValueOrInt(this BsonDocument doc, string name, int fallback = 0)
    {
        int result = fallback;
        if (doc.TryGetValue(name, out BsonValue value) && !value.IsBsonNull)
            result = ConvertToInt(value, fallback);
        return result;
    }

    internal static DateTime GetValueOrDate(this BsonDocument doc, string name, DateTime fallback)
    {
        DateTime result = fallback;
        if (doc.TryGetValue(name, out BsonValue value) && !value.IsBsonNull && value.IsValidDateTime)
            result = value.ToUniversalTime();
        return result;
    }

    internal static DateTime? GetValueOrNullableDate(this BsonDocument doc, string name)
    {
        DateTime? result = null;
        if (doc.TryGetValue(name, out BsonValue value) && !value.IsBsonNull && value.IsValidDateTime)
            result = value.ToUniversalTime();
        return result;
    }

    internal static TEnum GetValueOrEnum<TEnum>(this BsonDocument doc, string name, TEnum fallback) where TEnum : struct, Enum
    {
        TEnum result = fallback;
        if (doc.TryGetValue(name, out BsonValue value) && !value.IsBsonNull)
            result = ConvertToEnum(value, fallback);
        return result;
    }

    private static string ConvertToString(BsonValue value, string fallback) => value switch
    {
        BsonString s => s.Value,
        var _        => value.ToString() ?? fallback
    };

    private static int ConvertToInt(BsonValue value, int fallback) => value switch
    {
        BsonInt32 i32 => i32.Value,
        BsonInt64 i64 => (int) i64.Value,
        BsonDouble d  => (int) d.Value,
        var _         => fallback
    };

    private static TEnum ConvertToEnum<TEnum>(BsonValue value, TEnum fallback) where TEnum : struct, Enum => value switch
    {
        BsonInt32 i32 when Enum.IsDefined(typeof(TEnum), i32.Value) => (TEnum) Enum.ToObject(typeof(TEnum), i32.Value),
        BsonString s when Enum.TryParse<TEnum>(s.Value, ignoreCase: true, out TEnum parsed) => parsed,
        var _ => fallback
    };
}
