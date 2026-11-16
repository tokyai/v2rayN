using ReactiveUI.Builder;
using ServiceLib.Tests.CoreConfig;
using ServiceLib.ViewModels;

namespace ServiceLib.Tests.ViewModels;

/// <summary>验证主界面清理的组范围、搜索、选中节点、测速状态和重复操作。</summary>
/// <author>SHIXIAOYANG</author>
[NotInParallel]
public class ProfilesViewModelCleanupTests
{
    static ProfilesViewModelCleanupTests()
    {
        // 沿用项目已有的 ReactiveUI 测试初始化，命令和通知无需真实窗口。
        RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    }

    /// <summary>清理隐藏失败节点，同时保持可用节点的选中状态。</summary>
    [Test]
    public async Task ShouldClearHiddenFailedNodesAndKeepTheHealthySelection()
    {
        // await using 在成功或断言失败后都恢复本用例的数据和配置。
        await using var fixture = new ProfilesFixture();
        var failed = await fixture.AddProfile("hidden-failed", -1);
        var healthy = await fixture.AddProfile("visible-healthy", 20);
        var cancelled = await fixture.AddProfile("hidden-cancelled", 0);
        fixture.Config.IndexId = healthy.IndexId;
        var vm = await fixture.CreateViewModel();
        vm.ServerFilter = "visible-healthy";
        await vm.RefreshServersBiz();
        await vm.ProfileItems.Count.Should().BeEqualTo(1);

        await vm.ClearInvalidServers();

        await (await ConfigDbQuery.GetProfileItem(failed.IndexId)).Should().BeNull();
        await (await ConfigDbQuery.GetProfileItem(cancelled.IndexId)).Should().NotBeNull();
        await fixture.Config.IndexId.Should().BeEqualTo(healthy.IndexId);
        await vm.ProfileItems.Count.Should().BeEqualTo(1);
        await vm.ProfileItems[0].IndexId.Should().BeEqualTo(healthy.IndexId);

        // 清除搜索后重新排序，持久化列表和界面都只能包含剩余节点。
        vm.ServerFilter = "";
        await vm.SortServer(nameof(EServerColName.SpeedVal));
        await vm.RefreshServersBiz();
        await vm.ProfileItems.Count.Should().BeEqualTo(2);
        await vm.ProfileItems.Any(item => item.IndexId == failed.IndexId).Should().BeFalse();
    }

    /// <summary>全部组视图拒绝清理失败节点。</summary>
    [Test]
    public async Task ShouldRefuseCleanupInTheAllGroupsView()
    {
        // 全部组视图的 ID 为空，直接调用入口也必须保留失败节点。
        await using var fixture = new ProfilesFixture();
        var failed = await fixture.AddProfile("keep-all-groups", -1);
        var vm = await fixture.CreateViewModel();
        vm.SelectedSub = new SubItem { Remarks = ResUI.AllGroupServers };

        await vm.ClearInvalidServers();

        await (await ConfigDbQuery.GetProfileItem(failed.IndexId)).Should().NotBeNull();
    }

    /// <summary>测速取消后仍未结束时也拒绝清理。</summary>
    [Test]
    public async Task ShouldRefuseCleanupUntilTheRunningTestHasFinished()
    {
        // 空批次不访问网络；暂缓其完成通知，模拟仍在结束和释放资源的测速。
        await using var fixture = new ProfilesFixture();
        var failed = await fixture.AddProfile("keep-during-testing", -1);
        var vm = await fixture.CreateViewModel();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SpeedtestService(fixture.Config, result =>
        {
            entered.TrySetResult(true);
            return released.Task;
        });
        typeof(ProfilesViewModel).GetField("_speedtestService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, service);
        var run = service.RunLoop(ESpeedActionType.Tcping, []);

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await service.IsRunning.Should().BeTrue();
            await vm.ClearInvalidServers();
            vm.ServerSpeedtestStop();

            await service.IsRunning.Should().BeTrue();
            await (await ConfigDbQuery.GetProfileItem(failed.IndexId)).Should().NotBeNull();
        }
        finally
        {
            released.TrySetResult(true);
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await service.IsRunning.Should().BeFalse();
    }

    /// <summary>切组不改变已启动清理的范围，重复清理不启动第二批。</summary>
    [Test]
    public async Task ShouldKeepTheOriginalGroupSnapshotAndRejectADuplicateCleanup()
    {
        // 暂缓结果落盘锁，在清理等待期间切组并再次点清理。
        await using var fixture = new ProfilesFixture();
        var first = await fixture.AddProfile("first-group-failed", -1);
        var secondGroup = Guid.NewGuid().ToString("N");
        var second = await fixture.AddProfile("second-group-failed", -1, secondGroup);
        var vm = await fixture.CreateViewModel();
        var saveLock = (SemaphoreSlim)typeof(ProfileExManager)
            .GetField("_saveLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ProfileExManager.Instance)!;
        await saveLock.WaitAsync();
        var lockHeld = true;
        var clearing = vm.ClearInvalidServers();

        try
        {
            vm.SelectedSub = new SubItem { Id = secondGroup, Remarks = "Second cleanup group" };
            await vm.ClearInvalidServers();
            await (await ConfigDbQuery.GetProfileItem(first.IndexId)).Should().NotBeNull();
            await (await ConfigDbQuery.GetProfileItem(second.IndexId)).Should().NotBeNull();

            saveLock.Release();
            lockHeld = false;
            await clearing.WaitAsync(TimeSpan.FromSeconds(5));

            await (await ConfigDbQuery.GetProfileItem(first.IndexId)).Should().BeNull();
            await (await ConfigDbQuery.GetProfileItem(second.IndexId)).Should().NotBeNull();
        }
        finally
        {
            if (lockHeld)
            {
                saveLock.Release();
            }
            await clearing.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>测试配置和数据库记录仅使用本夹具生成的组 ID。</summary>
    private sealed class ProfilesFixture : IAsyncDisposable
    {
        private readonly Config _previousConfig = ConfigState.Instance.Current;
        private readonly HashSet<string> _groupIds = [];
        public Config Config { get; } = CoreConfigTestFactory.CreateConfig();
        public string GroupId { get; } = Guid.NewGuid().ToString("N");

        public ProfilesFixture()
        {
            TestDatabase.Initialize();
            Config.SubIndexId = GroupId;
            ConfigState.Instance.SetCurrent(Config);
            _groupIds.Add(GroupId);
        }

        /// <summary>在独立组中创建一个本地节点及指定延迟，不发起连接。</summary>
        public async Task<ProfileItem> AddProfile(string remarks, int delay, string? groupId = null)
        {
            var profile = new ProfileItem
            {
                IndexId = Guid.NewGuid().ToString("N"),
                Subid = groupId ?? GroupId,
                ConfigType = EConfigType.SOCKS,
                Address = "127.0.0.1",
                Port = 10808,
                Remarks = remarks,
            };
            _groupIds.Add(profile.Subid);
            await SQLiteHelper.Instance.InsertAsync(profile);
            ProfileExManager.Instance.SetTestDelay(profile.IndexId, delay);
            return profile;
        }

        /// <summary>建立测试组并让主界面读取真实数据库，不创建 Windows 窗口。</summary>
        public async Task<ProfilesViewModel> CreateViewModel()
        {
            foreach (var groupId in _groupIds)
            {
                await SQLiteHelper.Instance.InsertAsync(new SubItem { Id = groupId, Remarks = "Cleanup fixture" });
            }
            var vm = new ProfilesViewModel();
            await vm.RefreshSubscriptions();
            return vm;
        }

        /// <summary>删除本夹具剩余的节点和缓存，并恢复此前的测试配置。</summary>
        public async ValueTask DisposeAsync()
        {
            foreach (var groupId in _groupIds)
            {
                var profiles = await ConfigDbQuery.ProfileItems(groupId);
                foreach (var profile in profiles)
                {
                    ProfileExManager.Instance.SetTestDelay(profile.IndexId, -1);
                }
                await ProfileExManager.Instance.ClearInvalidServers(groupId);
                await SQLiteHelper.Instance.DeleteAsync(new SubItem { Id = groupId });
            }
            ConfigState.Instance.SetCurrent(_previousConfig);
        }
    }
}
