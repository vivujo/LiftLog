using System.Diagnostics;
using System.Text.Json;
using Fluxor;
using LiftLog.Lib.Serialization;
using LiftLog.Ui.Services;

namespace LiftLog.Ui.Store.CurrentSession
{
    public class PersistSessionMiddleware : Middleware
    {
        private const string Key = "CurrentSessionState";
        private IStore? _store;
        private readonly IKeyValueStore keyValueStore;

        public PersistSessionMiddleware(IKeyValueStore _keyValueStore)
        {
            keyValueStore = _keyValueStore;
        }

        public override async Task InitializeAsync(IDispatcher dispatch, IStore store)
        {
            _store = store;
            var currentSessionStateJson = await keyValueStore.GetItemAsync(Key);
            try
            {
                var currentSessionState = currentSessionStateJson != null
                    ? JsonSerializer.Deserialize<CurrentSessionState>(
                        currentSessionStateJson,
                        StorageJsonContext.Context.CurrentSessionState)
                    : null;
                if (currentSessionState is not null)
                {
                    store.Features["CurrentSession"].RestoreState(currentSessionState);
                }
            }
            catch (JsonException e)
            {
                Console.WriteLine("Failed to deserialize current session state", e);
            }
            dispatch.Dispatch(new RehydrateSessionAction());
        }

        public override async void AfterDispatch(object action)
        {
            var sw = Stopwatch.StartNew();
            var currentState = (CurrentSessionState?)_store?.Features["CurrentSession"].GetState();
            if (currentState != null)
            {
                var currentSessionState = JsonSerializer.Serialize(currentState, StorageJsonContext.Context.CurrentSessionState);
                var serializationTime = sw.ElapsedMilliseconds;
                await keyValueStore.SetItemAsync(Key, currentSessionState);
                sw.Stop();
                Console.WriteLine($"Persisted current session state in {sw.ElapsedMilliseconds}ms (serialization: {serializationTime}ms)");
            }
        }
    }
}
