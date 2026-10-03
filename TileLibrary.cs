using Godot;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // Folder-scan discovery of TileDefinition .tres files. Drop a file anywhere under a scan root
    // (subfolders included) → it appears. Lets a tile's definition live next to its mesh/texture. No master registry to maintain (that registry-maintenance was the GridMap pain).
    //
    // Used by both the renderer (resolve Id → mesh) and the palette dock (browse/search tiles).
    // Results are cached; call Refresh() after authoring a new tile.
    public static class TileLibrary
    {
        // Definitions live in the GAME, not the addon — they reference game meshes, and keeping them
        // out of the addon folder keeps submodule extraction clean. The addon only knows the paths.
        // Not "res://" — every .tres under a root gets loaded, so keep roots to asset folders.
        public static readonly string[] ScanRoots = { "res://Tiles/", "res://Entities/" };

        private static List<TileDefinition> _cache;

        public static IReadOnlyList<TileDefinition> GetAll()
        {
            // Rescan when null or empty. An empty result means Scan ran before C# types were
            // re-registered after hot-reload (ResourceLoader returned bare Resources, failing
            // the "is TileDefinition" check). Never permanently cache an empty result.
            if (_cache is not { Count: > 0 })
                _cache = ScanAll();
            return _cache;
        }

        public static IReadOnlyList<TileDefinition> Refresh()
        {
            _cache = ScanAll();
            return _cache;
        }

        private static List<TileDefinition> ScanAll()
        {
            var result = new List<TileDefinition>();
            foreach (string root in ScanRoots)
            {
                Scan(root, result);
            }
            return result;
        }

        // Recursive: walks every subfolder under dir, appending TileDefinitions to result.
        public static void Scan(string dir, List<TileDefinition> result)
        {
            using var da = DirAccess.Open(dir);
            if (da == null)
            {
                return;  // dir not created yet → empty, never a crash
            }

            da.ListDirBegin();
            for (string file = da.GetNext(); file != ""; file = da.GetNext())
            {
                if (da.CurrentIsDir())
                {
                    if (!file.StartsWith("."))
                    {
                        Scan(dir.PathJoin(file), result);
                    }
                    continue;
                }
                // Imported resources can surface as ".tres.remap" at export time; match both.
                if (!file.EndsWith(".tres") && !file.EndsWith(".tres.remap"))
                {
                    continue;
                }

                string path = dir.PathJoin(file.TrimSuffix(".remap"));
                if (ResourceLoader.Load(path) is TileDefinition def)
                {
                    result.Add(def);
                }
            }
            da.ListDirEnd();
        }
    }
}
