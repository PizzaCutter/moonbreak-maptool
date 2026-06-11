using Godot;

namespace Moonbreak.Maptool
{
    // Result of resolving a mouse ray against the map. Cell/Normal are in MapData cell coords.
    public struct PickResult
    {
        public bool Hit;          // false → ray hit nothing (off the active plane too)
        public Vector3I Cell;     // solid cell hit, or the plane cell under the cursor
        public Vector3I Normal;   // face the ray entered through (e.g. Up when landing on a floor)
        public bool FromPlane;    // true → resolved via active-layer plane, not a solid cell

        public static readonly PickResult Miss = new() { Hit = false };
    }

    // Pure-math cell picking. ONE mouse ray crosses many stacked cells; resolving with collision
    // shapes would force the renderer to spawn colliders and kill the future MultiMesh swap. So we
    // DDA voxel-march straight through MapData instead. All inputs are in the renderer's LOCAL space.
    public static class CellPicker
    {
        // localOrigin / localDir: ray already transformed into the renderer's local space.
        // cellSize: renderer cell size. activeLayer: Y of the build plane for void placement.
        // allowPlaneFallback: editor painting wants the active-layer plane catch (build into the
        // void); gameplay picking wants a clean miss when the ray leaves the terrain.
        public static PickResult Pick(MapData map, Vector3 localOrigin, Vector3 localDir,
                                      float cellSize, int activeLayer, bool allowPlaneFallback = true,
                                      int maxSteps = 256)
        {
            if (map == null || cellSize <= 0f)
            {
                return PickResult.Miss;
            }

            Vector3 dir = localDir.Normalized();
            // Work in cell units: one cell spans [c, c+1). Matches MapRenderer.CellToLocal.
            Vector3 origin = localOrigin / cellSize;

            var dda = MarchSolid(map, origin, dir, maxSteps);
            if (dda.Hit)
            {
                return dda;
            }

            if (!allowPlaneFallback)
            {
                return PickResult.Miss;
            }

            // Void fallback: intersect the active-layer plane so floors can be painted into empty space.
            return PlaneHit(origin, dir, activeLayer);
        }

        // Amanatides–Woo voxel traversal: step cell-by-cell along the ray, return the first solid cell.
        private static PickResult MarchSolid(MapData map, Vector3 origin, Vector3 dir, int maxSteps)
        {
            var cell = new Vector3I(Mathf.FloorToInt(origin.X), Mathf.FloorToInt(origin.Y), Mathf.FloorToInt(origin.Z));

            // Already standing inside a solid cell (camera buried in terrain) — rare; report it facing up.
            if (map.HasCell(cell))
            {
                return new PickResult { Hit = true, Cell = cell, Normal = Vector3I.Up, FromPlane = false };
            }

            var step = new Vector3I(SignOf(dir.X), SignOf(dir.Y), SignOf(dir.Z));
            Vector3 tMax = new(BoundaryT(origin.X, dir.X), BoundaryT(origin.Y, dir.Y), BoundaryT(origin.Z, dir.Z));
            Vector3 tDelta = new(DeltaT(dir.X), DeltaT(dir.Y), DeltaT(dir.Z));

            for (int i = 0; i < maxSteps; i++)
            {
                // Advance across the nearest cell boundary; the crossed axis defines the entry face.
                Vector3I enteredFace;
                if (tMax.X <= tMax.Y && tMax.X <= tMax.Z)
                {
                    if (step.X == 0) { break; }
                    cell.X += step.X; 
                    tMax.X += tDelta.X; 
                    enteredFace = new Vector3I(-step.X, 0, 0);
                }
                else if (tMax.Y <= tMax.Z)
                {
                    if (step.Y == 0) { break; }
                    cell.Y += step.Y; 
                    tMax.Y += tDelta.Y; 
                    enteredFace = new Vector3I(0, -step.Y, 0);
                }
                else
                {
                    if (step.Z == 0) { break; }
                    cell.Z += step.Z; 
                    tMax.Z += tDelta.Z; 
                    enteredFace = new Vector3I(0, 0, -step.Z);
                }

                if (map.HasCell(cell))
                {
                    return new PickResult { Hit = true, Cell = cell, Normal = enteredFace, FromPlane = false };
                }
            }
            return PickResult.Miss;
        }

        // Ray ∩ horizontal plane at the bottom of the active layer (local y = activeLayer in cell units).
        private static PickResult PlaneHit(Vector3 origin, Vector3 dir, int activeLayer)
        {
            if (Mathf.Abs(dir.Y) < 0.0001f)
            {
                return PickResult.Miss;  // ray parallel to plane
            }

            float t = (activeLayer - origin.Y) / dir.Y;
            if (t < 0f)
            {
                return PickResult.Miss;  // plane is behind the camera
            }

            Vector3 hit = origin + dir * t;
            var cell = new Vector3I(Mathf.FloorToInt(hit.X), activeLayer, Mathf.FloorToInt(hit.Z));
            return new PickResult { Hit = true, Cell = cell, Normal = Vector3I.Up, FromPlane = true };
        }

        private static int SignOf(float v) => v > 0f ? 1 : (v < 0f ? -1 : 0);

        // Parametric distance from origin to the first cell boundary along this axis.
        private static float BoundaryT(float originComp, float dirComp)
        {
            if (dirComp == 0f) { return float.PositiveInfinity; }
            float cell = Mathf.Floor(originComp);
            float next = dirComp > 0f ? cell + 1f : cell;     // boundary ahead in the step direction
            return (next - originComp) / dirComp;
        }

        // Parametric distance to cross one full cell along this axis.
        private static float DeltaT(float dirComp)
        {
            return dirComp == 0f ? float.PositiveInfinity : Mathf.Abs(1f / dirComp);
        }
    }
}
