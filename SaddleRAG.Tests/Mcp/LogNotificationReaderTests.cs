// LogNotificationReaderTests.cs
// Copyright © 2012–Present Jackalope Technologies, Inc. and Doug Gerard.
// SPDX-License-Identifier: MIT
// Licensed under the MIT License. See the LICENSE file in the repo root.

using NSubstitute;
using SaddleRAG.Core.Interfaces;
using SaddleRAG.Core.Models.Monitor;
using SaddleRAG.Mcp;

namespace SaddleRAG.Tests.Mcp;

public sealed class LogNotificationReaderTests
{
    [Theory]
    [InlineData(ServerLogLevel.Warning)]
    [InlineData(ServerLogLevel.Error)]
    public void ObservedProblemsSurviveLogRotationUntilAcknowledged(ServerLogLevel level)
    {
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        var log = Substitute.For<IServerLogReader>();
        log.Read(Arg.Any<int>()).Returns(ServerLogSnapshot.Empty with
        {
            Entries = [new ServerLogEntry { Timestamp = timestamp, Level = level, Message = "failure", DetailLines = [] }]
        });
        var acknowledgement = new ServerLogAcknowledgement();
        var notifications = new LogNotificationReader(log, acknowledgement);
        LogNotificationStatus observed = notifications.Read();
        Assert.Equal(level == ServerLogLevel.Error, observed.HasErrors);
        Assert.Equal(level == ServerLogLevel.Warning, observed.HasWarnings);

        log.Read(Arg.Any<int>()).Returns(ServerLogSnapshot.Empty);
        LogNotificationStatus rotated = notifications.Read();
        Assert.Equal(observed.HasErrors, rotated.HasErrors);
        Assert.Equal(observed.HasWarnings, rotated.HasWarnings);
        Assert.Equal(timestamp, rotated.NewestAt);

        acknowledgement.AcknowledgeThrough(timestamp);
        LogNotificationStatus cleared = notifications.Read();
        Assert.False(cleared.HasErrors);
        Assert.False(cleared.HasWarnings);
        Assert.Null(cleared.NewestAt);
    }

    [Fact]
    public void AcknowledgementDoesNotHideLaterErrors()
    {
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        var log = Substitute.For<IServerLogReader>();
        log.Read(Arg.Any<int>()).Returns(ServerLogSnapshot.Empty with
        {
            Entries = [new ServerLogEntry { Timestamp = timestamp, Level = ServerLogLevel.Error, Message = "failure", DetailLines = [] }]
        });
        var acknowledgement = new ServerLogAcknowledgement();
        acknowledgement.AcknowledgeThrough(timestamp.AddSeconds(-1));
        var notifications = new LogNotificationReader(log, acknowledgement);

        Assert.True(notifications.Read().HasErrors);
    }
}
