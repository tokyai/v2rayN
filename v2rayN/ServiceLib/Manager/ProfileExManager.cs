namespace ServiceLib.Manager;

public class ProfileExManager
{
    private static readonly Lazy<ProfileExManager> _instance = new(() => new());
    private ConcurrentDictionary<string, ProfileExItem> _dicProfileEx = new();
    private readonly ConcurrentQueue<string> _queIndexIds = new();
    private readonly Lock _profileExLock = new();
    // 清理与定时落盘共用此锁，避免旧落盘任务写回已删除节点的附属记录。
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private int _maxSort;
    public static ProfileExManager Instance => _instance.Value;
    private static readonly string _tag = "ProfileExHandler";

    public ProfileExManager()
    {
        //Init();
    }

    public async Task Init()
    {
        await InitData();
    }

    public async Task<ICollection<ProfileExItem>> GetProfileExs()
    {
        return await Task.FromResult(_dicProfileEx.Values);
    }

    private async Task InitData()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileExItem where indexId not in ( select indexId from ProfileItem )");

        var dicProfileEx = new ConcurrentDictionary<string, ProfileExItem>();
        foreach (var item in await SQLiteHelper.Instance.TableAsync<ProfileExItem>().ToListAsync())
        {
            dicProfileEx[item.IndexId] = item;
        }
        lock (_profileExLock)
        {
            _dicProfileEx = dicProfileEx;
            _maxSort = dicProfileEx.Values.Select(t => t.Sort).DefaultIfEmpty(0).Max();
        }
    }

    private void IndexIdEnqueue(string indexId)
    {
        if (indexId.IsNotEmpty())
        {
            //Speed tests touch this from many threads; the save loop deduplicates IDs.
            _queIndexIds.Enqueue(indexId);
        }
    }

    private async Task SaveQueueIndexIds()
    {
        var lstIds = new HashSet<string>();
        while (_queIndexIds.TryDequeue(out var id))
        {
            lstIds.Add(id);
        }

        if (lstIds.Count > 0)
        {
            List<ProfileExItem> lstSaves = [];

            foreach (var id in lstIds)
            {
                if (!_dicProfileEx.TryGetValue(id, out var itemNew))
                {
                    continue;
                }

                lstSaves.Add(itemNew);
            }

            try
            {
                if (lstSaves.Count > 0)
                {
                    //Another save may have inserted these rows since the queue was drained.
                    await SQLiteHelper.Instance.ReplaceAllAsync(lstSaves);
                }
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        }
    }

    private ProfileExItem AddProfileEx(string indexId)
    {
        var profileEx = new ProfileExItem()
        {
            IndexId = indexId,
            Delay = 0,
            Speed = 0,
            Sort = 0,
            Message = string.Empty
        };
        _dicProfileEx[indexId] = profileEx;
        IndexIdEnqueue(indexId);
        return profileEx;
    }

    private ProfileExItem GetProfileExItem(string? indexId)
    {
        indexId ??= string.Empty;
        if (_dicProfileEx.TryGetValue(indexId, out var profileEx))
        {
            return profileEx;
        }

        lock (_profileExLock)
        {
            return _dicProfileEx.TryGetValue(indexId, out profileEx) ? profileEx : AddProfileEx(indexId);
        }
    }

    public async Task ClearAll()
    {
        await SQLiteHelper.Instance.ExecuteAsync($"delete from ProfileExItem ");
        lock (_profileExLock)
        {
            _dicProfileEx = new();
            _maxSort = 0;
        }
    }

    /// <summary>将待更新的测速和排序记录落盘，与节点清理串行完成。</summary>
    public async Task SaveTo()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveQueueIndexIds();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>清理指定组内延迟已确认失败的节点，同时删除测速、排序记录和内存缓存。</summary>
    /// <param name="subId">本次清理固定的订阅组 ID；空 ID 不允许扩大为全部组。</param>
    /// <returns>实际删除的节点 ID；未测、取消未出结果和不支持测试的节点保留。</returns>
    public async Task<List<string>> ClearInvalidServers(string? subId)
    {
        if (string.IsNullOrWhiteSpace(subId))
        {
            return [];
        }

        // 此管理器拥有测速结果缓存和落盘队列，清理必须与这些任务串行完成。
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var profiles = await SQLiteHelper.Instance.TableAsync<ProfileItem>()
                .Where(profile => profile.Subid == subId).ToListAsync().ConfigureAwait(false);
            var removedIds = new List<string>();

            // 不使用界面搜索结果；直接删除真实节点，失败时事务保留原配置和组归属。
            await SQLiteHelper.Instance.RunInTransactionAsync(connection =>
            {
                foreach (var profile in profiles)
                {
                    if (profile.ConfigType.IsComplexType() || profile.Port <= 0 ||
                        !_dicProfileEx.TryGetValue(profile.IndexId, out var result) || result.Delay >= 0)
                    {
                        continue;
                    }

                    // 写入时再次核对组和配置类型，已移组或改成自定义配置的节点保留。
                    var count = connection.Execute(
                        "DELETE FROM ProfileItem WHERE IndexId = ? AND Subid = ? AND ConfigType = ? AND Port > 0",
                        profile.IndexId, subId, (int)profile.ConfigType);
                    if (count == 0)
                    {
                        continue;
                    }
                    connection.Execute("DELETE FROM ProfileExItem WHERE IndexId = ?", profile.IndexId);
                    removedIds.Add(profile.IndexId);
                }
            }).ConfigureAwait(false);

            // 只有事务成功后才移除缓存；队列中的旧 ID 随后落盘时会自然跳过。
            foreach (var indexId in removedIds)
            {
                _dicProfileEx.TryRemove(indexId, out _);
            }
            return removedIds;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    public void SetTestDelay(string indexId, int delay)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Delay = delay;
        IndexIdEnqueue(indexId);
    }

    public void SetTestSpeed(string indexId, decimal speed)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Speed = speed;
        IndexIdEnqueue(indexId);
    }

    public void SetTestMessage(string indexId, string message)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Message = message;
        IndexIdEnqueue(indexId);
    }

    public void SetTestIpInfo(string indexId, string ipInfo)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.IpInfo = ipInfo;
        IndexIdEnqueue(indexId);
    }

    public void SetSort(string indexId, int sort)
    {
        var profileEx = GetProfileExItem(indexId);

        profileEx.Sort = sort;
        IndexIdEnqueue(indexId);
        lock (_profileExLock)
        {
            _maxSort = Math.Max(_maxSort, sort);
        }
    }

    public int GetSort(string indexId)
    {
        return _dicProfileEx.TryGetValue(indexId, out var profileEx) ? profileEx.Sort : 0;
    }

    public int GetMaxSort()
    {
        return _maxSort;
    }
}
