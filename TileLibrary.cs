using Godot;
using System.Collections.Generic;

namespace Moonbreak.Maptool
{
    // Folder-scan discovery of TileDefinition .tres files. Drop a file in the definitions dir →
    // it appears. No master registry to maintain (that registry-maintenance was the GridMap pain).
    //
    // Used by both the renderer (resolve Id → mesh) and the palette dock (browse/search tiles).
    // Results are cached; call Refresh() after authoring a new tile.
    public static class TileLibrary
    {
        // Definitions live in the GAME, not the addon — they reference game meshes, and keeping them
        // out of the addon folder keeps submodule extraction clean. The addon only knows the path.
        public const string DefaultDir = "res://Tiles/Definitions/";

        private static List<TileDefinition> _cache;

        public static IReadOnlyList<TileDefinition> GetAll()
        {
            _cache ??= Scan(DefaultDir);
            return _cache;
        }

        public static IReadOnlyList<TileDefinition> Refresh()
        {
            _cache = Scan(DefaultDir);
            return _cache;
        }

        public static List<TileDefinition> Scan(string dir)
        {
            var result = new List<TileDefinition>();
            using var da = DirAccess.Open(dir);
            if (da == null)
            {
                return result;  // dir not created yet → empty, never a crash
            }

            da.ListDirBegin();
            for (string file = da.GetNext(); file != ""; file = da.GetNext())
            {
                if (da.CurrentIsDir())
                {
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
            return result;
        }
    }
}
