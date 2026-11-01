using System;
using System.Threading;

namespace Ntilde.VT.Tests;

public class ScrollbackRetentionLockTests
{
    private static TerminalBuffer FilledBuffer()
    {
        var buffer = new TerminalBuffer(20, 2);
        var parser = new AnsiParser(buffer);
        for (int i = 0; i < 200; i++) parser.Process($"row-{i}\r\n");
        Assert.True(buffer.Scrollback.Count > 128);
        return buffer;
    }

    private static void SetLimit(TerminalBuffer buffer, bool historyLimit, bool byteCeiling)
    {
        if (byteCeiling) buffer.MaxScrollbackBytesLimit = 1;
        else if (historyLimit) buffer.MaxHistory = 1;
        else buffer.MaxScrollbackBytes = 1;
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void RetentionMutation_WaitsForAnExistingReader(bool historyLimit, bool byteCeiling)
    {
        var buffer = FilledBuffer();
        using var completed = new ManualResetEventSlim();
        Exception? error = null;
        var writer = new Thread(() =>
        {
            try { SetLimit(buffer, historyLimit, byteCeiling); }
            catch (Exception ex) { error = ex; }
            finally { completed.Set(); }
        })
        { IsBackground = true };

        buffer.Lock.EnterReadLock();
        try
        {
            int beforeCount = buffer.Scrollback.Count;
            int beforeHistory = buffer.MaxHistory;
            long beforeBudget = buffer.MaxScrollbackBytes;
            long beforeCeiling = buffer.MaxScrollbackBytesLimit;
            var row = buffer.Scrollback.GetRow(0);
            char beforeCell = row[0].Character;
            writer.Start();
            Assert.True(SpinWait.SpinUntil(
                () => completed.IsSet || buffer.Lock.WaitingWriteCount > 0,
                TimeSpan.FromSeconds(10)), "Setter neither completed nor queued for the write lock.");
            Assert.False(completed.IsSet, "Retention changed while another thread held a read lock.");
            Assert.Equal(beforeHistory, buffer.MaxHistory);
            Assert.Equal(beforeBudget, buffer.MaxScrollbackBytes);
            Assert.Equal(beforeCeiling, buffer.MaxScrollbackBytesLimit);
            Assert.Equal(beforeCount, buffer.Scrollback.Count);
            Assert.Equal(beforeCell, row[0].Character);
        }
        finally
        {
            buffer.Lock.ExitReadLock();
            Assert.True(writer.Join(TimeSpan.FromSeconds(10)), "Setter did not finish after releasing the reader.");
        }
        Assert.Null(error);
        Assert.Equal(0, buffer.Scrollback.Count);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void RetentionMutation_ReusesTheCallersWriteLock(bool historyLimit, bool byteCeiling)
    {
        var buffer = FilledBuffer();
        buffer.Lock.EnterWriteLock();
        try
        {
            SetLimit(buffer, historyLimit, byteCeiling);
            Assert.True(buffer.Lock.IsWriteLockHeld);
            Assert.Equal(0, buffer.Scrollback.Count);
            SetLimit(buffer, historyLimit, byteCeiling);
            Assert.True(buffer.Lock.IsWriteLockHeld);
        }
        finally { buffer.Lock.ExitWriteLock(); }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void RetentionMutation_FromAReadLockThrowsBeforeChangingAnyState(bool historyLimit, bool byteCeiling)
    {
        var buffer = FilledBuffer();
        buffer.Lock.EnterReadLock();
        try
        {
            int beforeCount = buffer.Scrollback.Count;
            int beforeHistory = buffer.MaxHistory;
            long beforeBudget = buffer.MaxScrollbackBytes;
            long beforeCeiling = buffer.MaxScrollbackBytesLimit;
            Assert.Throws<LockRecursionException>(() => SetLimit(buffer, historyLimit, byteCeiling));
            Assert.True(buffer.Lock.IsReadLockHeld);
            Assert.Equal(beforeCount, buffer.Scrollback.Count);
            Assert.Equal(beforeHistory, buffer.MaxHistory);
            Assert.Equal(beforeBudget, buffer.MaxScrollbackBytes);
            Assert.Equal(beforeCeiling, buffer.MaxScrollbackBytesLimit);
        }
        finally { buffer.Lock.ExitReadLock(); }
    }
}
