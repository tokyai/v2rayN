using ServiceLib.Tests.CoreConfig;

namespace ServiceLib.Tests.Manager;

/// <summary>使用独立组和节点验证清理边界、事务回滚及订阅重新导入。</summary>
/// <author>SHIXIAOYANG</author>
[NotInParallel]
public class ProfileCleanupTests
{
    /// <summary>延迟失败可删除，其余状态及其他组均保留。</summary>
    [Test]
    public async Task ShouldClearOnlyFailedDelaysFromTheCurrentGroup()
    {
        // await using 保证断言失败时也清理本用例创建的数据。
        await using var fixture = new CleanupFixture();
        var failed = await fixture.AddProfile("failed", -1);
        var failedAgain = await fixture.AddProfile("failed-negative", -2);
        var healthy = await fixture.AddProfile("healthy", 20, 5);
        await fixture.AddProfile("download-failed", 20, -1);
        await fixture.AddProfile("untested", null);
        await fixture.AddProfile("cancelled", 0);
        await fixture.AddProfile("custom", -1, type: EConfigType.Custom);
        await fixture.AddProfile("unsupported-port", -1, port: 0);
        await fixture.AddProfile("policy-group", -1, type: EConfigType.PolicyGroup);
        var otherGroup = await fixture.AddProfile("other-group", -1, subId: Guid.NewGuid().ToString("N"));
        await fixture.Manager.SaveTo();

        // 清理从整个持久化分组读取，不接收界面过滤或选中行参数。
        var removed = await fixture.Manager.ClearInvalidServers(fixture.GroupId);

        await removed.Count.Should().BeEqualTo(2);
        await removed.Contains(failed.IndexId).Should().BeTrue();
        await removed.Contains(failedAgain.IndexId).Should().BeTrue();
        var remaining = await ConfigDbQuery.ProfileItems(fixture.GroupId);
        await remaining.Count.Should().BeEqualTo(7);
        await remaining.Any(profile => profile.IndexId == healthy.IndexId).Should().BeTrue();
        await (await ConfigDbQuery.GetProfileItem(otherGroup.IndexId)).Should().NotBeNull();
        await (await ConfigDbQuery.GetProfileItem(failed.IndexId)).Should().BeNull();
        await (await SQLiteHelper.Instance.TableAsync<ProfileExItem>()
            .Where(item => item.IndexId == failed.IndexId).CountAsync()).Should().BeEqualTo(0);
    }

    /// <summary>缺少组 ID 时不执行任何删除。</summary>
    [Test]
    public async Task ShouldKeepAllGroupsWhenTheCleanupGroupIsMissing()
    {
        // 全部组使用空 ID，不能让空值被数据库查询解释成全量删除。
        await using var fixture = new CleanupFixture();
        var failed = await fixture.AddProfile("keep-failed", -1);

        foreach (var subId in new string?[] { null, "", " " })
        {
            var removed = await fixture.Manager.ClearInvalidServers(subId);
            await removed.Count.Should().BeEqualTo(0);
        }

        await (await ConfigDbQuery.GetProfileItem(failed.IndexId)).Should().NotBeNull();
    }

    /// <summary>无清理目标时返回零删除数量。</summary>
    [Test]
    public async Task ShouldReturnZeroForAnEmptyGroupOrAGroupWithoutFailedDelays()
    {
        // 空组和仅包含可用节点的组都返回真实的零删除数量。
        await using var fixture = new CleanupFixture();
        var empty = await fixture.Manager.ClearInvalidServers(fixture.GroupId);
        var healthy = await fixture.AddProfile("keep-healthy", 20, 5);

        var removed = await fixture.Manager.ClearInvalidServers(fixture.GroupId);

        await empty.Count.Should().BeEqualTo(0);
        await removed.Count.Should().BeEqualTo(0);
        await (await ConfigDbQuery.GetProfileItem(healthy.IndexId)).Should().NotBeNull();
    }

    /// <summary>附属记录删除失败时恢复同批节点配置和结果。</summary>
    [Test]
    public async Task ShouldRollbackNodeDeletionWhenDeletingItsTestRecordFails()
    {
        // 附属记录的删除触发失败；同一事务中的节点配置删除必须一起回滚。
        await using var fixture = new CleanupFixture();
        var first = await fixture.AddProfile("first-failed", -1);
        var second = await fixture.AddProfile("second-failed", -1);
        await fixture.Manager.SaveTo();
        var trigger = $"CleanupFailure_{Guid.NewGuid():N}";
        await SQLiteHelper.Instance.ExecuteAsync(
            $"CREATE TRIGGER {trigger} BEFORE DELETE ON ProfileExItem " +
            $"WHEN OLD.IndexId = '{second.IndexId}' BEGIN SELECT RAISE(ABORT, 'cleanup fixture failure'); END");

        try
        {
            SQLiteException? failure = null;
            try
            {
                await fixture.Manager.ClearInvalidServers(fixture.GroupId);
            }
            catch (SQLiteException ex)
            {
                failure = ex;
            }

            await failure.Should().NotBeNull();
            var profiles = await ConfigDbQuery.ProfileItems(fixture.GroupId);
            await profiles.Count.Should().BeEqualTo(2);
            await profiles.All(profile => profile.Subid == fixture.GroupId).Should().BeTrue();
            await (await SQLiteHelper.Instance.TableAsync<ProfileExItem>()
                .Where(item => item.IndexId == first.IndexId || item.IndexId == second.IndexId)
                .CountAsync()).Should().BeEqualTo(2);
            var results = await fixture.Manager.GetProfileExs();
            await results.Any(item => item.IndexId == first.IndexId && item.Delay == -1).Should().BeTrue();
            await results.Any(item => item.IndexId == second.IndexId && item.Delay == -1).Should().BeTrue();
        }
        finally
        {
            await SQLiteHelper.Instance.ExecuteAsync($"DROP TRIGGER IF EXISTS {trigger}");
        }
    }

    /// <summary>落盘队列不能写回已清理节点的记录。</summary>
    [Test]
    public async Task ShouldNotWriteRemovedNodesBackWhenPendingResultsAreSaved()
    {
        // 失败节点仍有待落盘的测速和排序 ID，清理后落盘只能保存剩余节点。
        await using var fixture = new CleanupFixture();
        var failed = await fixture.AddProfile("pending-failed", -1);
        var healthy = await fixture.AddProfile("pending-healthy", 20, 5);
        fixture.Manager.SetSort(failed.IndexId, 9);
        fixture.Manager.SetSort(healthy.IndexId, 10);

        await fixture.Manager.ClearInvalidServers(fixture.GroupId);
        await fixture.Manager.SaveTo();

        var remaining = await ConfigDbQuery.ProfileItems(fixture.GroupId);
        await remaining.Count.Should().BeEqualTo(1);
        await remaining[0].IndexId.Should().BeEqualTo(healthy.IndexId);
        await (await SQLiteHelper.Instance.TableAsync<ProfileExItem>()
            .Where(item => item.IndexId == failed.IndexId).CountAsync()).Should().BeEqualTo(0);
        var saved = await SQLiteHelper.Instance.TableAsync<ProfileExItem>()
            .Where(item => item.IndexId == healthy.IndexId).FirstAsync();
        await saved.Delay.Should().BeEqualTo(20);
        await saved.Speed.Should().BeEqualTo(5m);
        await saved.Sort.Should().BeEqualTo(10);
    }

    /// <summary>含 SQL 字符的组 ID 不改变清理范围。</summary>
    [Test]
    public async Task ShouldTreatSubscriptionIdsAsValuesInsteadOfSql()
    {
        // 含引号的组 ID 仍是普通标识，不能扩大到另一组。
        await using var fixture = new CleanupFixture();
        var targetGroup = $"{fixture.GroupId}' OR 1=1 --";
        var failed = await fixture.AddProfile("quoted-group", -1, subId: targetGroup);
        var other = await fixture.AddProfile("keep-other-group", -1);

        var removed = await fixture.Manager.ClearInvalidServers(targetGroup);

        await removed.Count.Should().BeEqualTo(1);
        await removed[0].Should().BeEqualTo(failed.IndexId);
        await (await ConfigDbQuery.GetProfileItem(other.IndexId)).Should().NotBeNull();
    }

    /// <summary>手动更新完整订阅时重新导入此前清理的节点。</summary>
    [Test]
    public async Task ShouldReimportTheWholeSubscriptionAfterCleanup()
    {
        // 使用本地订阅文本，验证清理不创建永久过滤名单，也不影响现有全量导入。
        await using var fixture = new CleanupFixture();
        var previousConfig = ConfigState.Instance.Current;
        ConfigState.Instance.SetCurrent(CoreConfigTestFactory.CreateConfig());
        var contents = "socks://127.0.0.1:1#Cleanup-failed\nsocks://127.0.0.1:18080#Cleanup-healthy";
        await SQLiteHelper.Instance.InsertAsync(new SubItem
        {
            Id = fixture.GroupId,
            Remarks = "Cleanup subscription fixture",
            Url = "http://127.0.0.1/subscription",
        });

        try
        {
            var originalCount = await ConfigDbHandler.AddBatchServers(contents, fixture.GroupId, true);
            var original = await ConfigDbQuery.ProfileItems(fixture.GroupId);
            var failed = original.First(profile => profile.Remarks == "Cleanup-failed");
            fixture.Manager.SetTestDelay(failed.IndexId, -1);

            await fixture.Manager.ClearInvalidServers(fixture.GroupId);
            var restoredCount = await ConfigDbHandler.AddBatchServers(contents, fixture.GroupId, true);

            var restored = await ConfigDbQuery.ProfileItems(fixture.GroupId);
            await originalCount.Should().BeEqualTo(2);
            await restoredCount.Should().BeEqualTo(2);
            await restored.Count.Should().BeEqualTo(2);
            await restored.Any(profile => profile.Remarks == "Cleanup-failed").Should().BeTrue();
            await restored.Any(profile => profile.IndexId == failed.IndexId).Should().BeFalse();
            await (await fixture.Manager.ClearInvalidServers(fixture.GroupId)).Count.Should().BeEqualTo(0);
        }
        finally
        {
            ConfigState.Instance.SetCurrent(previousConfig);
            await SQLiteHelper.Instance.DeleteAsync(new SubItem { Id = fixture.GroupId });
        }
    }

    /// <summary>此夹具只拥有自己生成的组 ID，结束时删除其节点和附属记录。</summary>
    private sealed class CleanupFixture : IAsyncDisposable
    {
        private readonly HashSet<string> _groupIds = [];
        public string GroupId { get; } = Guid.NewGuid().ToString("N");
        public ProfileExManager Manager { get; } = new();

        public CleanupFixture()
        {
            TestDatabase.Initialize();
            _groupIds.Add(GroupId);
        }

        /// <summary>建立测试节点，可选择未测、失败或成功的测速状态。</summary>
        /// <param name="name">用于区分测试节点的备注。</param>
        /// <param name="delay">延迟结果；空值表示尚未创建结果记录。</param>
        /// <param name="speed">下载速度结果，可独立于延迟失败。</param>
        /// <param name="type">用于验证测试支持范围的节点类型。</param>
        /// <param name="port">用于验证无可测试端口时保留节点。</param>
        /// <param name="subId">覆盖默认测试组以验证跨组隔离。</param>
        /// <returns>本次建立的独立测试节点。</returns>
        public async Task<ProfileItem> AddProfile(string name, int? delay, decimal speed = 0,
            EConfigType type = EConfigType.SOCKS, int port = 10808, string? subId = null)
        {
            var profile = new ProfileItem
            {
                IndexId = Guid.NewGuid().ToString("N"),
                Subid = subId ?? GroupId,
                ConfigType = type,
                Remarks = name,
                Address = "127.0.0.1",
                Port = port,
            };
            _groupIds.Add(profile.Subid);
            await SQLiteHelper.Instance.InsertAsync(profile);
            if (delay.HasValue)
            {
                Manager.SetTestDelay(profile.IndexId, delay.Value);
                Manager.SetTestSpeed(profile.IndexId, speed);
            }
            return profile;
        }

        /// <summary>移除本夹具所属组中的剩余节点和附属记录。</summary>
        public async ValueTask DisposeAsync()
        {
            // 删除目标始终由本夹具固定的组 ID 决定，不清空共享测试数据库。
            foreach (var groupId in _groupIds)
            {
                var profiles = await ConfigDbQuery.ProfileItems(groupId);
                foreach (var profile in profiles)
                {
                    await SQLiteHelper.Instance.DeleteAsync(profile);
                    await SQLiteHelper.Instance.DeleteAsync(new ProfileExItem { IndexId = profile.IndexId });
                }
            }
        }
    }
}
