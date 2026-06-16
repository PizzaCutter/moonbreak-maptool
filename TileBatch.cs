using Godot;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // One MultiMesh drawing every cell that shares a single tile mesh. Cells live in a dense,
    // swap-compacted buffer so add/remove stay O(1) amortized — no per-cell Node, one draw call
    // for the whole batch. Capacity grows in chunks because changing MultiMesh.InstanceCount
    // reallocates and clears the buffer, so we re-upload all transforms only on a grow, never on
    // a plain add. VisibleInstanceCount tracks the live count without touching capacity.
    [Tool]
    public partial class TileBatch : MultiMeshInstance3D
    {
        // Parallel arrays in buffer order. _xforms is kept so a capacity grow (which wipes the
        // GPU-side buffer) can re-upload everything; cells map an instance slot back to its cell.
        private readonly List<Vector3I> _cells = new();
        private readonly List<Transform3D> _xforms = new();
        private int _capacity;

        public void Init(Mesh mesh)
        {
            Multimesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = mesh,
                InstanceCount = 0,
            };
            _capacity = 0;
        }

        public int Count => _cells.Count;

        // Append a cell. Returns its instance index.
        public int Add(Vector3I cell, Transform3D xform)
        {
            int index = _cells.Count;
            _cells.Add(cell);
            _xforms.Add(xform);
            EnsureCapacity(_cells.Count);
            Multimesh.VisibleInstanceCount = _cells.Count;
            Multimesh.SetInstanceTransform(index, xform);
            return index;
        }

        // Swap-pop remove: move the last instance into the freed slot so the buffer stays dense.
        // Returns the cell that got moved into `index` (so the owner can fix its index map), or
        // null if the removed cell was already the last one.
        public Vector3I? RemoveAt(int index)
        {
            int last = _cells.Count - 1;
            Vector3I? moved = null;
            if (index != last)
            {
                _cells[index] = _cells[last];
                _xforms[index] = _xforms[last];
                Multimesh.SetInstanceTransform(index, _xforms[index]);
                moved = _cells[index];
            }
            _cells.RemoveAt(last);
            _xforms.RemoveAt(last);
            Multimesh.VisibleInstanceCount = _cells.Count;
            return moved;
        }

        private void EnsureCapacity(int needed)
        {
            if (needed <= _capacity)
            {
                return;
            }
            int newCap = Mathf.Max(needed, _capacity == 0 ? 64 : _capacity * 2);
            Multimesh.InstanceCount = newCap;  // destructive → re-upload existing transforms below
            _capacity = newCap;
            for (int i = 0; i < _xforms.Count; i++)
            {
                Multimesh.SetInstanceTransform(i, _xforms[i]);
            }
        }
    }
}
