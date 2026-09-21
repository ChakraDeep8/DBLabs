using System.Collections.Generic;
using System.Linq;
using DBLabs.Models.Roi;

namespace DBLabs.Services
{
    /// <summary>
    /// Snapshot-based undo/redo over the calibration object list. Simple and robust:
    /// every mutating action (create/delete/move/resize/rename/reorder) calls
    /// Snapshot(before-state) first, so Undo/Redo just swap whole object-list snapshots.
    /// </summary>
    public class RoiHistory
    {
        private readonly Stack<List<CalibrationObjectBase>> _undo = new();
        private readonly Stack<List<CalibrationObjectBase>> _redo = new();

        public bool CanUndo => _undo.Count > 0;
        public bool CanRedo => _redo.Count > 0;

        public void Snapshot(IEnumerable<CalibrationObjectBase> current)
        {
            _undo.Push(Clone(current));
            _redo.Clear();
        }

        public List<CalibrationObjectBase>? Undo(IEnumerable<CalibrationObjectBase> current)
        {
            if (_undo.Count == 0) return null;
            _redo.Push(Clone(current));
            return _undo.Pop();
        }

        public List<CalibrationObjectBase>? Redo(IEnumerable<CalibrationObjectBase> current)
        {
            if (_redo.Count == 0) return null;
            _undo.Push(Clone(current));
            return _redo.Pop();
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
        }

        private static List<CalibrationObjectBase> Clone(IEnumerable<CalibrationObjectBase> source) =>
            source.Select(o => o.Clone()).ToList();
    }
}
