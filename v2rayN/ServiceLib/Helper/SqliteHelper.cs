using System.Diagnostics.CodeAnalysis;

namespace ServiceLib.Helper;

public sealed class SQLiteHelper
{
    private static readonly Lazy<SQLiteHelper> _instance = new(() => new());
    public static SQLiteHelper Instance => _instance.Value;
    private readonly string _connstr;
    private SQLiteConnection _db;
    private SQLiteAsyncConnection _dbAsync;
    private readonly string _configDB = "guiNDB.db";

    public SQLiteHelper()
    {
        _connstr = Utils.GetConfigPath(_configDB);
        _db = new SQLiteConnection(_connstr, false);
        _dbAsync = new SQLiteAsyncConnection(_connstr, false);
    }

    public CreateTableResult CreateTable<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
        return _db.CreateTable<T>();
    }

    public async Task<int> InsertAllAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(IEnumerable<T> models)
    {
        var count = 0;
        await _dbAsync.RunInTransactionAsync(connection =>
        {
            foreach (var model in models)
            {
                count += connection.Insert(model, typeof(T));
            }
        }).ConfigureAwait(false);
        return count;
    }

    public async Task<int> ReplaceAllAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(IEnumerable<T> models)
    {
        var count = 0;
        await _dbAsync.RunInTransactionAsync(connection =>
        {
            foreach (var model in models)
            {
                count += connection.InsertOrReplace(model, typeof(T));
            }
        }).ConfigureAwait(false);
        return count;
    }

    public async Task<int> InsertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T model)
    {
        return await _dbAsync.InsertAsync(model, typeof(T));
    }

    public async Task<int> ReplaceAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T model)
    {
        return await _dbAsync.InsertOrReplaceAsync(model, typeof(T));
    }

    public async Task<int> UpdateAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T model)
    {
        return await _dbAsync.UpdateAsync(model, typeof(T));
    }

    public async Task<int> UpdateAllAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(IEnumerable<T> models)
    {
        var count = 0;
        await _dbAsync.RunInTransactionAsync(connection =>
        {
            foreach (var model in models)
            {
                count += connection.Update(model, typeof(T));
            }
        }).ConfigureAwait(false);
        return count;
    }

    /// <summary>将一组数据库修改共同提交；任一步骤抛出异常时自动回滚整组修改。</summary>
    /// <param name="action">必须一起成功的节点及附属记录修改。</param>
    /// <returns>事务提交或回滚完成后结束。</returns>
    public Task RunInTransactionAsync(Action<SQLiteConnection> action)
    {
        // 复用异步连接的事务和锁，数据库操作在其工作线程执行。
        return _dbAsync.RunInTransactionAsync(action);
    }

    public async Task<int> DeleteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(T model)
    {
        var mapping = await _dbAsync.GetMappingAsync(typeof(T));
        var primaryKey = mapping.PK ?? throw new NotSupportedException($"Cannot delete {mapping.TableName}: it has no PK");
        return await _dbAsync.DeleteAsync(primaryKey.GetValue(model), mapping);
    }

    public async Task<int> DeleteAllAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>()
    {
        return await _dbAsync.DeleteAllAsync<T>();
    }

    public async Task<int> ExecuteAsync(string sql)
    {
        return await _dbAsync.ExecuteAsync(sql);
    }

    public async Task<List<T>> QueryAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>(string sql) where T : new()
    {
        return await _dbAsync.QueryAsync<T>(sql);
    }

    public AsyncTableQuery<T> TableAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>() where T : new()
    {
        return _dbAsync.Table<T>();
    }

    public async Task DisposeDbConnectionAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _db?.Close();
                _db?.Dispose();
            }
            finally
            {
                _db = null;
            }

            try
            {
                var conn = _dbAsync?.GetConnection();
                conn?.Close();
                conn?.Dispose();
            }
            finally
            {
                _dbAsync = null;
            }
        });
    }
}
