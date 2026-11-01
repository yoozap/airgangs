using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UIElements;
using TbilisiFly.UI.Common;
using TbilisiFly.Combat;
using TbilisiFly.Core;

namespace TbilisiFly.UI.Combat
{
    /// <summary>
    /// Port of src/pickups.js:~29-103 — the ground-loot card stack: one card
    /// per weapon/gear piece in reach, picture + name + caliber + "F —
    /// აღება", nearest one lit, built once per item and kept (pickups.js:72
    /// `_card(it)`'s own comment: "Built once per item and kept").
    ///
    /// THE DECISION already lives in <see cref="Pickups"/> (Combat/
    /// Pickups.cs:180 <c>VisibleCards</c>) — its own header calls the DOM
    /// stack "/UI/'s territory" and explicitly names <c>VisibleCards</c> "the
    /// same kind of snapshot" PaperTargets.ScoreSnapshot/RangeLane.LaneHud
    /// are. <c>Core/HeldWeapon.cs</c> only reads <c>Pickups.Near</c> for the
    /// F-key gameplay check — nothing renders <c>VisibleCards</c> before this
    /// view.
    ///
    /// Georgian/formatted strings taken verbatim: the caliber line is
    /// <see cref="Pickups.Item.Caliber"/> itself (already ported, pickups.js
    /// :77-80), the take hint is pickups.js:83 "F — აღება", and gear's own
    /// caliber-line text is pickups.js:80 "აღჭურვილობა — ჩაიცვამს".
    ///
    /// THUMBNAILS: pickups.js:81 sources `it.def.thumb` (public/models/thumbs
    /// /&lt;id&gt;.png over HTTP). No Resources/Addressables pipeline exists
    /// in this project yet (same gap Core/GameLoop.cs's EnsureMapUi() already
    /// works around with a #if UNITY_EDITOR AssetDatabase load) — the same 4
    /// PNGs are imported under Assets/Resources/UI/Thumbs/&lt;id&gt;.png (this pass) and
    /// loaded the same way. A missing/]not-yet-imported thumbnail renders no
    /// image, matching pickups.js:81's own `src="${it.def.thumb ?? ''}"`
    /// (a blank img on a miss, not a placeholder).
    /// </summary>
    public sealed class PickupCardsView : VisualElement
    {
        // pickups.js:48 linear-gradient(180deg, rgba(6,16,24,.82), rgba(4,10,16,.9)) — flattened
        static readonly Color PanelBg = new Color(5f / 255f, 13f / 255f, 20f / 255f, 0.86f);
        static readonly Color BorderDim = CyberTheme.Hex("#5adcff", 0.28f);  // pickups.js:49
        static readonly Color BorderFocus = CyberTheme.Hex("#5adcff", 0.6f); // pickups.js:52
        static readonly Color NameColor = CyberTheme.Hex("#dff6ff");        // pickups.js:57
        static readonly Color CalColor = CyberTheme.Hex("#8fe3b0");         // pickups.js:59
        static readonly Color TakeColor = CyberTheme.Hex("#7fd4ef");        // pickups.js:61
        static readonly Color KeyBorder = CyberTheme.Hex("#5adcff", 0.6f);  // pickups.js:64
        static readonly Color KeyBg = CyberTheme.Hex("#5adcff", 0.12f);     // pickups.js:64
        static readonly Color KeyText = CyberTheme.Hex("#eaffff");          // pickups.js:64

        internal readonly Dictionary<Pickups.Item, VisualElement> _cards = new Dictionary<Pickups.Item, VisualElement>();
        static readonly Dictionary<string, Texture2D> _thumbCache = new Dictionary<string, Texture2D>();
        string _shownKey = "";

        /// <summary>pickups.js:86 `el.addEventListener('pointerdown', ... this.take(it))`.
        /// Wire to <c>Pickups.Take(item)</c>.</summary>
        public System.Action<Pickups.Item> OnTakeClicked;

        public PickupCardsView()
        {
            style.position = Position.Absolute;
            style.left = new Length(50, LengthUnit.Percent);            // pickups.js:44
            style.bottom = new Length(21, LengthUnit.Percent);
            style.translate = new Translate(new Length(-50, LengthUnit.Percent), 0);
            style.flexDirection = FlexDirection.Column;
            style.alignItems = Align.Stretch;
            style.display = DisplayStyle.None;
        }

        /// <summary>pickups.js:93-100 `_render(list)` — only touches the
        /// hierarchy when the set/order of items actually changed, same key
        /// trick as the JS's `list.map(it=>it.obj.id).join(',')` (reference
        /// identity stands in for the JS's Object3D id here).</summary>
        public void Render(IReadOnlyList<Pickups.Item> items)
        {
            string key = string.Join(",", items.Select(RuntimeHelpers.GetHashCode));
            if (key == _shownKey) return;
            _shownKey = key;

            Clear();
            for (int i = 0; i < items.Count; i++)
            {
                var card = CardFor(items[i]);
                SetFocus(card, i == 0); // pickups.js:98 — nearest (index 0) is lit
                Add(card);
            }
            style.display = items.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None; // pickups.js:99
        }

        VisualElement CardFor(Pickups.Item it)
        {
            if (_cards.TryGetValue(it, out var el)) return el;
            el = BuildCard(it);
            _cards[it] = el;
            return el;
        }

        VisualElement BuildCard(Pickups.Item it)
        {
            var card = new VisualElement();
            card.style.flexDirection = FlexDirection.Row;
            card.style.alignItems = Align.Center;
            card.style.paddingTop = 8; card.style.paddingBottom = 8;     // pickups.js:47
            card.style.paddingLeft = 10; card.style.paddingRight = 16;
            card.style.marginBottom = 7;                                  // pickups.js:45 gap substitute
            card.style.backgroundColor = PanelBg;
            card.style.borderTopLeftRadius = card.style.borderTopRightRadius =
                card.style.borderBottomLeftRadius = card.style.borderBottomRightRadius = 10; // pickups.js:49
            card.style.borderTopWidth = card.style.borderBottomWidth =
                card.style.borderLeftWidth = card.style.borderRightWidth = 1;

            string id = it.IsGear ? it.Gear.Id : it.Weapon?.Id;
            var img = new Image { image = ThumbFor(id), scaleMode = ScaleMode.ScaleToFit, pickingMode = PickingMode.Ignore };
            img.style.width = img.style.height = 64;                       // pickups.js:54
            card.Add(img);

            var text = new VisualElement { pickingMode = PickingMode.Ignore };
            text.style.marginLeft = 12;                                    // pickups.js:47 gap
            card.Add(text);

            var name = new Label(it.Name) { pickingMode = PickingMode.Ignore }; // pickups.js:84
            name.style.color = NameColor;
            name.style.fontSize = 15;
            name.style.letterSpacing = 0.4f;
            text.Add(name);

            var cal = new Label(it.Caliber()) { pickingMode = PickingMode.Ignore }; // pickups.js:85
            cal.style.color = CalColor;
            cal.style.fontSize = 11.5f;
            cal.style.marginTop = 3;
            cal.style.letterSpacing = 0.3f;
            text.Add(cal);

            var take = new VisualElement { pickingMode = PickingMode.Ignore };
            take.style.flexDirection = FlexDirection.Row;
            take.style.alignItems = Align.Center;
            take.style.marginTop = 4;
            text.Add(take);

            var keyB = new Label("F") { pickingMode = PickingMode.Ignore };  // pickups.js:83 <b>
            keyB.style.minWidth = 18;
            keyB.style.unityTextAlign = TextAnchor.MiddleCenter;
            keyB.style.paddingTop = 1; keyB.style.paddingBottom = 1;
            keyB.style.paddingLeft = 5; keyB.style.paddingRight = 5;
            keyB.style.marginRight = 4;
            keyB.style.borderTopWidth = keyB.style.borderBottomWidth =
                keyB.style.borderLeftWidth = keyB.style.borderRightWidth = 1;
            keyB.style.borderTopColor = keyB.style.borderBottomColor =
                keyB.style.borderLeftColor = keyB.style.borderRightColor = KeyBorder;
            keyB.style.borderTopLeftRadius = keyB.style.borderTopRightRadius =
                keyB.style.borderBottomLeftRadius = keyB.style.borderBottomRightRadius = 4;
            keyB.style.backgroundColor = KeyBg;
            keyB.style.color = KeyText;
            take.Add(keyB);

            var takeLabel = new Label("— აღება") { pickingMode = PickingMode.Ignore }; // pickups.js:83
            takeLabel.style.color = TakeColor;
            takeLabel.style.fontSize = 12;
            take.Add(takeLabel);

            card.RegisterCallback<PointerDownEvent>(evt => { evt.StopPropagation(); OnTakeClicked?.Invoke(it); }); // pickups.js:86

            // stash references the focus-toggle (and the probe) need without re-querying
            card.userData = new CardRefs { Img = img, Take = takeLabel, Border = card, Name = name, Cal = cal, KeyBadge = keyB };
            return card;
        }

        internal sealed class CardRefs
        {
            public Image Img; public Label Take; public VisualElement Border;
            public Label Name, Cal, KeyBadge;
        }

        /// <summary>Probe hook — the built card's internal refs, keyed the
        /// same way <see cref="_cards"/> is.</summary>
        internal CardRefs RefsFor(Pickups.Item it) => _cards.TryGetValue(it, out var el) ? (CardRefs)el.userData : null;

        void SetFocus(VisualElement card, bool focus)
        {
            var r = (CardRefs)card.userData;
            card.style.opacity = focus ? 1f : 0.78f;                       // pickups.js:51,53
            card.style.borderTopColor = card.style.borderBottomColor =
                card.style.borderLeftColor = card.style.borderRightColor = focus ? BorderFocus : BorderDim;
            r.Img.style.width = r.Img.style.height = focus ? 72 : 64;      // pickups.js:56
            r.Take.style.opacity = focus ? 0.9f : 0.55f;                   // pickups.js:61-62
        }

        static Texture2D ThumbFor(string id)
        {
            if (id == null) return null;
            if (_thumbCache.TryGetValue(id, out var t)) return t;
            t = ArtAssets.Load<Texture2D>($"Assets/Resources/UI/Thumbs/{id}.png");
            _thumbCache[id] = t;
            return t;
        }
    }
}
