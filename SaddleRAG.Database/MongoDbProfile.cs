// MongoDbProfile.cs

// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: AGPL-3.0-or-later OR LicenseRef-SaddleRAG-Commercial
// Available under AGPLv3 (see LICENSE) or a commercial license
// (see COMMERCIAL-LICENSE.md). Contact douglas@jackalopetechnologies.com.



namespace SaddleRAG.Database;



/// <summary>

///     A named MongoDB connection profile.

/// </summary>

public class MongoDbProfile

{

    /// <summary>

    ///     MongoDB connection string for this profile.

    /// </summary>

    public string ConnectionString { get; set; } = DefaultConnectionString;



    /// <summary>

    ///     Database name for this profile.

    /// </summary>

    public string DatabaseName { get; set; } = DefaultDatabaseName;



    /// <summary>

    ///     Human-readable description of this profile.

    /// </summary>

    public string? Description { get; set; }


    internal const string DefaultConnectionString = "mongodb://localhost:27017";
    internal const string DefaultDatabaseName = "SaddleRAG";
}
