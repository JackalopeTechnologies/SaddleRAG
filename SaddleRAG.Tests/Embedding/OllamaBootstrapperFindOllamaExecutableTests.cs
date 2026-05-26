// OllamaBootstrapperFindOllamaExecutableTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT

#region Usings

using SaddleRAG.Ingestion.Embedding;

#endregion

namespace SaddleRAG.Tests.Embedding;

public sealed class OllamaBootstrapperFindOllamaExecutableTests
{
    [Fact]
    public void ReturnsFirstHitInPathEnvironment()
    {
        var dirs = new[] { Dir("usr", "local", "bin"), Dir("usr", "bin"), Dir("opt", "bin") };
        var target = Path.Combine(dirs[1], "ollama");
        bool FileExists(string p) => p == target;

        var result = OllamaBootstrapper.FindOllamaExecutable(string.Join(Path.PathSeparator, dirs),
                                                             FileExists,
                                                             exeName: "ollama",
                                                             commonPaths: []
                                                            );

        Assert.Equal(target, result);
    }

    [Fact]
    public void ReturnsEarliestPathHitWhenMultipleDirsContainExe()
    {
        var dirs = new[] { Dir("first", "bin"), Dir("second", "bin") };
        var first = Path.Combine(dirs[0], "ollama");
        var second = Path.Combine(dirs[1], "ollama");
        bool FileExists(string p) => p == first || p == second;

        var result = OllamaBootstrapper.FindOllamaExecutable(string.Join(Path.PathSeparator, dirs),
                                                             FileExists,
                                                             exeName: "ollama",
                                                             commonPaths: []
                                                            );

        Assert.Equal(first, result);
    }

    [Fact]
    public void FallsBackToCommonPathsWhenPathContainsNoMatch()
    {
        var dirs = new[] { Dir("no", "match"), Dir("also", "no") };
        var common = new[] { Dir("opt", "ollama-binary"), Path.Combine(Dir("usr", "local", "bin"), "ollama") };
        bool FileExists(string p) => p == common[1];

        var result = OllamaBootstrapper.FindOllamaExecutable(string.Join(Path.PathSeparator, dirs),
                                                             FileExists,
                                                             exeName: "ollama",
                                                             commonPaths: common
                                                            );

        Assert.Equal(common[1], result);
    }

    [Fact]
    public void ReturnsEarliestCommonPathHit()
    {
        var common = new[] { Dir("first"), Dir("second"), Dir("third") };
        bool FileExists(string p) => p == common[1] || p == common[2];

        var result = OllamaBootstrapper.FindOllamaExecutable(pathEnvValue: null,
                                                             FileExists,
                                                             exeName: "ollama",
                                                             commonPaths: common
                                                            );

        Assert.Equal(common[1], result);
    }

    [Fact]
    public void ReturnsNullWhenPathIsNullAndCommonPathsAllMiss()
    {
        var result = OllamaBootstrapper.FindOllamaExecutable(pathEnvValue: null,
                                                             fileExists: _ => false,
                                                             exeName: "ollama",
                                                             commonPaths: [Dir("x", "missing")]
                                                            );

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNullWhenNoSearchableLocationsExist()
    {
        var result = OllamaBootstrapper.FindOllamaExecutable(pathEnvValue: null,
                                                             fileExists: _ => false,
                                                             exeName: "ollama",
                                                             commonPaths: []
                                                            );

        Assert.Null(result);
    }

    [Fact]
    public void SkipsEmptyPathSegments()
    {
        // Empty segments (consecutive separators, leading/trailing) must
        // not be searched — Path.Combine with "" would produce a bare
        // exe-name probe that could accidentally hit a file in CWD.
        var sep = Path.PathSeparator;
        var hit = Dir("usr", "bin");
        var pathEnv = $"{sep}{Dir("no", "match")}{sep}{sep}{hit}{sep}";
        var target = Path.Combine(hit, "ollama");
        var probed = new List<string>();
        bool FileExists(string p)
        {
            probed.Add(p);
            return p == target;
        }

        var result = OllamaBootstrapper.FindOllamaExecutable(pathEnv,
                                                             FileExists,
                                                             exeName: "ollama",
                                                             commonPaths: []
                                                            );

        Assert.Equal(target, result);
        Assert.DoesNotContain("ollama", probed);
    }

    [Fact]
    public void ThrowsWhenExeNameEmpty()
    {
        Assert.Throws<ArgumentException>(() =>
            OllamaBootstrapper.FindOllamaExecutable(pathEnvValue: null,
                                                    fileExists: _ => false,
                                                    exeName: string.Empty,
                                                    commonPaths: []
                                                   ));
    }

    // Builds a platform-appropriate directory path so tests run on both
    // Linux (forward slashes) and Windows (backslashes). Using
    // Path.Combine here also matches what FindOllamaExecutable invokes
    // when joining dir + exeName during probe construction.
    private static string Dir(params string[] segments) => Path.Combine([Path.DirectorySeparatorChar.ToString(), .. segments]);
}
