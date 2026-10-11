using System;

namespace LOMSave2
{
    // ExecuteLoadGameData/ExecuteSaveData are shared by manual, automatic and
    // internal game operations. Only their synchronous manual entry points own
    // a sidecar. A scope also restores the caller's context after an exception.
    public static class LomsIoContext
    {
        [ThreadStatic] private static string _loadSlot;
        [ThreadStatic] private static string _saveSlot;

        public static string ManualLoadSlot => _loadSlot;
        public static string ManualSaveSlot => _saveSlot;

        public static Scope EnterLoad(string slot) => new Scope(true, slot);
        public static Scope EnterSave(string slot) => new Scope(false, slot);

        public sealed class Scope : IDisposable
        {
            private readonly bool _load;
            private readonly string _previous;
            private bool _disposed;

            internal Scope(bool load, string slot)
            {
                _load = load;
                _previous = load ? _loadSlot : _saveSlot;
                if (load) _loadSlot = slot;
                else _saveSlot = slot;
            }

            public void Dispose()
            {
                if (_disposed) return;
                if (_load) _loadSlot = _previous;
                else _saveSlot = _previous;
                _disposed = true;
            }
        }
    }
}
