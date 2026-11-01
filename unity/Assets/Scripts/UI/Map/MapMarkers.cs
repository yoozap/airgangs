namespace TbilisiFly.UI.Map
{
    /// <summary>Ported verbatim from src/mapui.js:741-778. Georgian labels byte-for-byte.</summary>
    public static class MapMarkers
    {
        public struct Statue { public double Lat, Lon; public string Label; }

        // mapui.js:741-752 STATUE_MARKERS — the ten hand-modeled monuments.
        public static readonly Statue[] Statues =
        {
            new Statue { Lat = 41.69339, Lon = 44.8015,   Label = "წმ. გიორგი" },
            new Statue { Lat = 41.68812, Lon = 44.8046,   Label = "ქართლის დედა" },
            new Statue { Lat = 41.69032, Lon = 44.81073,  Label = "გორგასალი" },
            new Statue { Lat = 41.70389, Lon = 44.78993,  Label = "რუსთაველი" },
            new Statue { Lat = 41.76157, Lon = 44.7686,   Label = "დავით აღმაშენებელი" },
            new Statue { Lat = 41.76249, Lon = 44.76732,  Label = "წმ. ნინო" },
            new Statue { Lat = 41.7245,  Lon = 44.7765,   Label = "სააკაძე" },
            new Statue { Lat = 41.72384, Lon = 44.72901,  Label = "ვაჟა-ფშაველა" },
            new Statue { Lat = 41.69775, Lon = 44.79818,  Label = "წერეთელი და ჭავჭავაძე" },
            new Statue { Lat = 41.68883, Lon = 44.82045,  Label = "ფარნავაზი" },
        };

        // mapui.js:754-778 METRO_MARKERS — the 23 station coordinates (no labels; just "M").
        public static readonly (double lat, double lon)[] MetroStations =
        {
            (41.68770, 44.82705), (41.68647, 44.84088), (41.70418, 44.78971), (41.69496, 44.80014),
            (41.73262, 44.79700), (41.70983, 44.79754), (41.72245, 44.71778), (41.79157, 44.81565),
            (41.78357, 44.79949), (41.77510, 44.79539), (41.74264, 44.78461), (41.68544, 44.85533),
            (41.71989, 44.77835), (41.72709, 44.76319), (41.72552, 44.74457), (41.72424, 44.73093),
            (41.72707, 44.78800), (41.74978, 44.77946), (41.76551, 44.79023), (41.69250, 44.81513),
            (41.72198, 44.79790), (41.72252, 44.79608), (41.69129, 44.87094),
        };
    }
}
