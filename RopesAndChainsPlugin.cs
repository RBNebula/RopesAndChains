namespace RopesAndChains;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class RopesAndChainsPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.ryanb.ropesandchains";
    public const string PluginName = "Ropes and Chains";
    public const string PluginVersion = "0.2.0";

    private static readonly FieldInfo? GrabJointField = AccessTools.Field(typeof(PlayerController), "_grabJoint");
    private static readonly FieldInfo? InteractRangeField = AccessTools.Field(typeof(PlayerController), "_interactRange");

    private static ManualLogSource? s_log;
    private static readonly System.Collections.Generic.HashSet<RopesAndChainsState> ActiveHangStates = new System.Collections.Generic.HashSet<RopesAndChainsState>();
    private Harmony? _harmony;

    internal static ConfigEntry<bool>? Enabled;
    internal static ConfigEntry<float>? HangRayDistance;
    internal static ConfigEntry<float>? RopeSpring;
    internal static ConfigEntry<float>? RopeDamper;
    internal static ConfigEntry<float>? MinRopeLength;
    internal static ConfigEntry<float>? MaxRopeLength;
    internal static ConfigEntry<float>? AnchorSurfaceOffset;
    internal static ConfigEntry<bool>? KeepAwakeWhenHung;
    internal static ConfigEntry<bool>? VisualEnabled;
    internal static ConfigEntry<string>? VisualStyle;
    internal static ConfigEntry<float>? VisualThickness;
    internal static ConfigEntry<string>? VisualColorHex;
    internal static ConfigEntry<float>? LinkLength;
    internal static ConfigEntry<int>? MinLinks;
    internal static ConfigEntry<int>? MaxLinks;
    internal static ConfigEntry<float>? ObjectEndInset;
    internal static ConfigEntry<bool>? AnchorModuleEnabled;
    internal static ConfigEntry<float>? AnchorModuleScale;

    private void Awake()
    {
        s_log = Logger;

        Enabled = Config.Bind("General", "Enabled", true, "Enable rope hang behavior on Grab key.");
        HangRayDistance = Config.Bind("General", "HangRayDistance", 8f, "How far you can target map geometry to hang an object.");
        RopeSpring = Config.Bind("Physics", "RopeSpring", 65f, "Spring strength used for hung objects.");
        RopeDamper = Config.Bind("Physics", "RopeDamper", 9f, "Damping used for hung objects.");
        MinRopeLength = Config.Bind("Physics", "MinRopeLength", 0.2f, "Minimum rope length.");
        MaxRopeLength = Config.Bind("Physics", "MaxRopeLength", 15f, "Maximum rope length.");
        AnchorSurfaceOffset = Config.Bind("Physics", "AnchorSurfaceOffset", 0.02f, "Offset from hit surface to avoid clipping.");
        KeepAwakeWhenHung = Config.Bind("Physics", "KeepAwakeWhenHung", true, "Prevent sleeping/freeze while an object is hung.");
        VisualEnabled = Config.Bind("Visual", "Enabled", true, "Render a mesh rope/chain for hung objects.");
        VisualStyle = Config.Bind("Visual", "Style", "Chain", "Visual style: None, Rope, Chain.");
        VisualThickness = Config.Bind("Visual", "Thickness", 0.035f, "Thickness/radius for the visual rope/chain.");
        VisualColorHex = Config.Bind("Visual", "ColorHex", "#7A6A52", "Hex color for the visual, e.g. #7A6A52.");
        LinkLength = Config.Bind("Visual", "LinkLength", 0.18f, "Length per link/segment. [ and ] change by exactly one LinkLength.");
        MinLinks = Config.Bind("Visual", "MinLinks", 1, "Minimum link count.");
        MaxLinks = Config.Bind("Visual", "MaxLinks", 200, "Maximum link count.");
        ObjectEndInset = Config.Bind("Visual", "ObjectEndInset", 0.06f, "How far the last link visually sinks into the hung object.");
        AnchorModuleEnabled = Config.Bind("Visual", "AnchorModuleEnabled", true, "Show a hanger module at the terrain anchor.");
        AnchorModuleScale = Config.Bind("Visual", "AnchorModuleScale", 1f, "Scale multiplier for the terrain hanger module.");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(RopesAndChainsPlugin).Assembly);
        Logger.LogInfo($"{PluginName} {PluginVersion} initialized.");
    }

    private void Update()
    {
        if (Enabled?.Value != true)
        {
            return;
        }

        var cycleStylePressed = Input.GetKeyDown(KeyCode.Alpha3) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        var increasePressed = Input.GetKeyDown(KeyCode.RightBracket);
        var decreasePressed = Input.GetKeyDown(KeyCode.LeftBracket);
        if (!cycleStylePressed && !increasePressed && !decreasePressed)
        {
            return;
        }

        var ui = Singleton<UIManager>.Instance;
        if (ui != null && ui.IsInAnyMenu())
        {
            return;
        }

        if (cycleStylePressed)
        {
            CycleVisualStyle();
        }

        if (!increasePressed && !decreasePressed)
        {
            return;
        }

        if (!TryGetLookedAtHangState(out var state))
        {
            return;
        }

        if (increasePressed)
        {
            state.AdjustLinks(+1);
        }
        else if (decreasePressed)
        {
            state.AdjustLinks(-1);
        }
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    internal static bool TryConvertGrabToHang(PlayerController player)
    {
        if (Enabled?.Value != true)
        {
            return false;
        }

        if (player.HeldObject == null || GrabJointField == null)
        {
            return false;
        }

        var grabJoint = GrabJointField.GetValue(player) as SpringJoint;
        if (grabJoint == null)
        {
            return false;
        }

        var cam = player.PlayerCamera;
        if (cam == null)
        {
            return false;
        }

        var baseRange = 2f;
        if (InteractRangeField != null && InteractRangeField.GetValue(player) is float f)
        {
            baseRange = f;
        }

        var rayDistance = Mathf.Max(baseRange, HangRayDistance?.Value ?? 8f);
        if (!Physics.Raycast(cam.transform.position, cam.transform.forward, out var hit, rayDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        if (!IsValidHangTarget(hit, player.HeldObject))
        {
            return false;
        }

        var body = player.HeldObject.GetComponent<Rigidbody>();
        if (body == null)
        {
            return false;
        }

        var localAnchor = grabJoint.connectedAnchor;
        var worldAnchorOnObject = body.transform.TransformPoint(localAnchor);
        var targetAnchor = hit.point + hit.normal * (AnchorSurfaceOffset?.Value ?? 0.02f);
        var targetLength = Mathf.Clamp(
            Vector3.Distance(worldAnchorOnObject, targetAnchor),
            MinRopeLength?.Value ?? 0.2f,
            MaxRopeLength?.Value ?? 15f);

        player.ReleaseObject();

        if (body == null || body.gameObject == null)
        {
            return true;
        }

        var state = body.GetComponent<RopesAndChainsState>();
        if (state == null)
        {
            state = body.gameObject.AddComponent<RopesAndChainsState>();
        }

        state.Attach(body, localAnchor, targetAnchor, targetLength, RopeSpring?.Value ?? 65f, RopeDamper?.Value ?? 9f);
        return true;
    }

    private static bool TryGetLookedAtHangState(out RopesAndChainsState state)
    {
        state = null!;
        var player = UnityEngine.Object.FindObjectOfType<PlayerController>();
        if (player == null || player.PlayerCamera == null)
        {
            return false;
        }

        var distance = Mathf.Max(2f, HangRayDistance?.Value ?? 8f);
        if (!Physics.Raycast(player.PlayerCamera.transform.position, player.PlayerCamera.transform.forward, out var hit, distance, ~0, QueryTriggerInteraction.Ignore))
        {
            return false;
        }

        state = hit.rigidbody != null
            ? hit.rigidbody.GetComponent<RopesAndChainsState>()
            : hit.collider != null ? hit.collider.GetComponentInParent<RopesAndChainsState>() : null!;

        return state != null;
    }

    private void CycleVisualStyle()
    {
        if (VisualStyle == null)
        {
            return;
        }

        var current = VisualStyle.Value?.Trim();
        var next = string.Equals(current, "Rope", StringComparison.OrdinalIgnoreCase) ? "Chain" : "Rope";
        VisualStyle.Value = next;

        var snapshot = new RopesAndChainsState[ActiveHangStates.Count];
        ActiveHangStates.CopyTo(snapshot);
        for (var i = 0; i < snapshot.Length; i++)
        {
            if (snapshot[i] != null)
            {
                snapshot[i].RefreshVisualSettings();
            }
        }

        Logger.LogInfo($"Visual style switched to {next}.");
    }

    internal static void RegisterHangState(RopesAndChainsState state)
    {
        ActiveHangStates.Add(state);
    }

    internal static void UnregisterHangState(RopesAndChainsState state)
    {
        ActiveHangStates.Remove(state);
    }

    internal static void DetachHangFromGrabbedTarget(RaycastHit hit)
    {
        var target = hit.rigidbody != null
            ? hit.rigidbody.GetComponent<RopesAndChainsState>()
            : hit.collider != null ? hit.collider.GetComponentInParent<RopesAndChainsState>() : null;

        target?.Detach();
        if (target != null)
        {
            UnityEngine.Object.Destroy(target);
        }
    }

    private static bool IsValidHangTarget(RaycastHit hit, GameObject heldObject)
    {
        var col = hit.collider;
        if (col == null || col.isTrigger)
        {
            return false;
        }

        if (col.CompareTag("Grabbable"))
        {
            return false;
        }

        if (col.transform.IsChildOf(heldObject.transform))
        {
            return false;
        }

        var rb = hit.rigidbody ?? col.attachedRigidbody;
        if (rb != null && !rb.isKinematic)
        {
            return false;
        }

        return true;
    }

    internal static void LogError(string context, Exception ex)
    {
        (s_log ?? BepInEx.Logging.Logger.CreateLogSource(PluginName)).LogError($"{context}: {ex}");
    }
}

[HarmonyPatch(typeof(PlayerController), "TryGrabObject")]
internal static class PlayerControllerTryGrabObjectPatch
{
    private static bool Prefix(PlayerController __instance)
    {
        try
        {
            if (RopesAndChainsPlugin.TryConvertGrabToHang(__instance))
            {
                return false;
            }
        }
        catch (Exception ex)
        {
            RopesAndChainsPlugin.LogError("TryGrabObject prefix failed", ex);
        }

        return true;
    }
}

[HarmonyPatch(typeof(PlayerController), "GrabObject")]
internal static class PlayerControllerGrabObjectPatch
{
    private static void Prefix(RaycastHit hit)
    {
        try
        {
            RopesAndChainsPlugin.DetachHangFromGrabbedTarget(hit);
        }
        catch (Exception ex)
        {
            RopesAndChainsPlugin.LogError("GrabObject prefix failed", ex);
        }
    }
}

internal sealed class RopesAndChainsState : MonoBehaviour
{
    private Rigidbody? _body;
    private Rigidbody? _anchorBody;
    private SpringJoint? _joint;
    private RopesAndChainsVisual? _visual;
    private float _originalSleepThreshold;
    private int _linkCount;
    private int _minLinks;
    private int _maxLinks;
    private float _linkLength;
    private float _objectEndInset;

    public void Attach(Rigidbody body, Vector3 localAnchor, Vector3 worldAnchor, float ropeLength, float spring, float damper)
    {
        Detach();

        _body = body;
        _originalSleepThreshold = body.sleepThreshold;
        body.sleepThreshold = 0f;
        body.WakeUp();

        var anchorObject = new GameObject("RopesAndChainsAnchor");
        anchorObject.transform.position = worldAnchor;
        _anchorBody = anchorObject.AddComponent<Rigidbody>();
        _anchorBody.isKinematic = true;
        _anchorBody.useGravity = false;
        _anchorBody.detectCollisions = false;

        _joint = gameObject.AddComponent<SpringJoint>();
        _joint.autoConfigureConnectedAnchor = false;
        _joint.anchor = localAnchor;
        _joint.connectedBody = _anchorBody;
        _joint.connectedAnchor = Vector3.zero;
        _joint.minDistance = 0f;
        _joint.spring = spring;
        _joint.damper = damper;
        _joint.enableCollision = false;
        _joint.breakForce = float.PositiveInfinity;
        _joint.breakTorque = float.PositiveInfinity;
        _linkLength = Mathf.Clamp(RopesAndChainsPlugin.LinkLength?.Value ?? 0.18f, 0.02f, 2f);
        _minLinks = Mathf.Clamp(RopesAndChainsPlugin.MinLinks?.Value ?? 1, 1, 10000);
        _maxLinks = Mathf.Clamp(RopesAndChainsPlugin.MaxLinks?.Value ?? 200, _minLinks, 10000);
        _objectEndInset = Mathf.Clamp(RopesAndChainsPlugin.ObjectEndInset?.Value ?? 0.06f, 0f, 2f);
        _linkCount = Mathf.Clamp(Mathf.Max(1, Mathf.RoundToInt(ropeLength / _linkLength)), _minLinks, _maxLinks);
        ApplyLengthFromLinks();
        RopesAndChainsPlugin.RegisterHangState(this);
        RefreshVisualSettings();
    }

    public void RefreshVisualSettings()
    {
        var style = ParseStyle(RopesAndChainsPlugin.VisualStyle?.Value);
        if (RopesAndChainsPlugin.VisualEnabled?.Value != true || style == RopeVisualStyle.None)
        {
            if (_visual != null)
            {
                _visual.ClearVisuals();
                UnityEngine.Object.Destroy(_visual);
                _visual = null;
            }
            return;
        }

        _visual ??= gameObject.GetComponent<RopesAndChainsVisual>() ?? gameObject.AddComponent<RopesAndChainsVisual>();
        _visual.Setup(
            style,
            Mathf.Clamp(RopesAndChainsPlugin.VisualThickness?.Value ?? 0.035f, 0.005f, 0.25f),
            ParseColor(RopesAndChainsPlugin.VisualColorHex?.Value),
            RopesAndChainsPlugin.AnchorModuleEnabled?.Value == true,
            Mathf.Clamp(RopesAndChainsPlugin.AnchorModuleScale?.Value ?? 1f, 0.2f, 5f));
    }

    public void AdjustLinks(int delta)
    {
        if (_joint == null || delta == 0)
        {
            return;
        }

        _linkCount = Mathf.Clamp(_linkCount + delta, _minLinks, _maxLinks);
        ApplyLengthFromLinks();
        _body?.WakeUp();
    }

    private void ApplyLengthFromLinks()
    {
        if (_joint == null)
        {
            return;
        }

        _joint.maxDistance = Mathf.Max(0.01f, _linkCount * _linkLength);
    }

    public void Detach()
    {
        RopesAndChainsPlugin.UnregisterHangState(this);

        if (_joint != null)
        {
            UnityEngine.Object.Destroy(_joint);
            _joint = null;
        }

        if (_anchorBody != null)
        {
            UnityEngine.Object.Destroy(_anchorBody.gameObject);
            _anchorBody = null;
        }

        if (_body != null)
        {
            _body.sleepThreshold = _originalSleepThreshold;
            _body = null;
        }

        if (_visual != null)
        {
            _visual.ClearVisuals();
            UnityEngine.Object.Destroy(_visual);
            _visual = null;
        }
    }

    private void FixedUpdate()
    {
        if (_body == null)
        {
            return;
        }

        if (RopesAndChainsPlugin.KeepAwakeWhenHung?.Value == true && _body.IsSleeping())
        {
            _body.WakeUp();
        }
    }

    private void LateUpdate()
    {
        if (_visual == null || _body == null || _anchorBody == null || _joint == null)
        {
            return;
        }

        var objectAnchorWorld = _body.transform.TransformPoint(_joint.anchor);
        var worldAnchor = _anchorBody.transform.position;
        _visual.UpdateVisual(worldAnchor, objectAnchorWorld, _linkCount, _objectEndInset);
    }

    private static RopeVisualStyle ParseStyle(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return RopeVisualStyle.Chain;
        }

        if (text.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return RopeVisualStyle.None;
        }

        if (text.Equals("rope", StringComparison.OrdinalIgnoreCase))
        {
            return RopeVisualStyle.Rope;
        }

        return RopeVisualStyle.Chain;
    }

    private static Color ParseColor(string? text)
    {
        if (!string.IsNullOrWhiteSpace(text) && ColorUtility.TryParseHtmlString(text, out var parsed))
        {
            return parsed;
        }

        return new Color(0.48f, 0.41f, 0.32f, 1f);
    }

    private void OnDestroy()
    {
        Detach();
    }
}

internal enum RopeVisualStyle
{
    None,
    Rope,
    Chain
}

internal sealed class RopesAndChainsVisual : MonoBehaviour
{
    private readonly System.Collections.Generic.List<Transform> _segments = new System.Collections.Generic.List<Transform>();
    private RopeVisualStyle _style = RopeVisualStyle.Chain;
    private float _thickness = 0.035f;
    private Material? _material;
    private Transform? _anchorModule;

    public void Setup(RopeVisualStyle style, float thickness, Color color, bool showAnchorModule, float anchorModuleScale)
    {
        ClearVisuals();
        _style = style;
        _thickness = Mathf.Clamp(thickness, 0.005f, 0.25f);

        var shader = Shader.Find("Standard");
        if (shader != null)
        {
            _material = new Material(shader);
            _material.color = color;
        }

        if (showAnchorModule)
        {
            CreateAnchorModule(Mathf.Clamp(anchorModuleScale, 0.2f, 5f));
        }
    }

    private void CreateAnchorModule(float scale)
    {
        var root = new GameObject("RopesAndChainsModule");
        root.transform.SetParent(transform, worldPositionStays: true);
        _anchorModule = root.transform;

        var cap = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        cap.name = "ModuleCap";
        cap.transform.SetParent(root.transform, worldPositionStays: false);
        cap.transform.localPosition = Vector3.zero;
        cap.transform.localRotation = Quaternion.identity;
        cap.transform.localScale = new Vector3(0.18f, 0.02f, 0.18f) * scale;
        RemoveColliderAndApplyMaterial(cap);

        var stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        stem.name = "ModuleStem";
        stem.transform.SetParent(root.transform, worldPositionStays: false);
        stem.transform.localPosition = new Vector3(0f, -0.055f * scale, 0f);
        stem.transform.localRotation = Quaternion.identity;
        stem.transform.localScale = new Vector3(0.05f, 0.04f, 0.05f) * scale;
        RemoveColliderAndApplyMaterial(stem);
    }

    private void RemoveColliderAndApplyMaterial(GameObject go)
    {
        var col = go.GetComponent<Collider>();
        if (col != null)
        {
            UnityEngine.Object.Destroy(col);
        }

        var renderer = go.GetComponent<Renderer>();
        if (renderer != null && _material != null)
        {
            renderer.sharedMaterial = _material;
        }
    }

    private void EnsureSegmentCount(int targetCount)
    {
        var clamped = Mathf.Clamp(targetCount, 0, 512);
        while (_segments.Count > clamped)
        {
            var idx = _segments.Count - 1;
            var seg = _segments[idx];
            _segments.RemoveAt(idx);
            if (seg != null)
            {
                UnityEngine.Object.Destroy(seg.gameObject);
            }
        }

        while (_segments.Count < clamped)
        {
            var i = _segments.Count;
            var primitive = GameObject.CreatePrimitive(_style == RopeVisualStyle.Rope ? PrimitiveType.Cylinder : PrimitiveType.Capsule);
            primitive.name = _style == RopeVisualStyle.Rope ? $"RopeSeg_{i}" : $"ChainLink_{i}";
            primitive.transform.SetParent(transform, worldPositionStays: true);
            RemoveColliderAndApplyMaterial(primitive);
            _segments.Add(primitive.transform);
        }
    }

    public void UpdateVisual(Vector3 start, Vector3 end, int linkCount, float objectEndInset)
    {
        var vector = end - start;
        var distance = vector.magnitude;
        if (_anchorModule != null)
        {
            _anchorModule.position = start;
            if (distance > 0.001f)
            {
                _anchorModule.rotation = Quaternion.FromToRotation(Vector3.down, vector.normalized);
            }
        }

        if (_style == RopeVisualStyle.None)
        {
            EnsureSegmentCount(0);
            return;
        }

        EnsureSegmentCount(linkCount);
        if (_segments.Count == 0)
        {
            return;
        }

        var dir = distance > 0.001f ? vector / distance : Vector3.down;
        var visualEnd = end + dir * Mathf.Max(0f, objectEndInset);
        vector = visualEnd - start;
        distance = vector.magnitude;
        if (distance < 0.001f)
        {
            for (var i = 0; i < _segments.Count; i++)
            {
                _segments[i].gameObject.SetActive(false);
            }

            return;
        }

        dir = vector / distance;
        var look = Quaternion.FromToRotation(Vector3.up, dir);
        var pieceLength = distance / _segments.Count;
        var halfPiece = pieceLength * 0.5f;
        var spacing = 1f / _segments.Count;

        for (var i = 0; i < _segments.Count; i++)
        {
            var seg = _segments[i];
            seg.gameObject.SetActive(true);

            var t = (i + 0.5f) * spacing;
            seg.position = Vector3.Lerp(start, visualEnd, t);

            if (_style == RopeVisualStyle.Rope)
            {
                seg.rotation = look;
                seg.localScale = new Vector3(_thickness * 2f, halfPiece, _thickness * 2f);
                continue;
            }

            var linkRot = look * Quaternion.AngleAxis((i % 2 == 0) ? 0f : 90f, Vector3.up);
            seg.rotation = linkRot;
            seg.localScale = new Vector3(_thickness * 1.35f, Mathf.Max(_thickness * 1.7f, halfPiece * 0.9f), _thickness * 1.35f);
        }
    }

    public void ClearVisuals()
    {
        for (var i = 0; i < _segments.Count; i++)
        {
            if (_segments[i] != null)
            {
                UnityEngine.Object.Destroy(_segments[i].gameObject);
            }
        }

        _segments.Clear();
        if (_anchorModule != null)
        {
            UnityEngine.Object.Destroy(_anchorModule.gameObject);
            _anchorModule = null;
        }
        if (_material != null)
        {
            UnityEngine.Object.Destroy(_material);
            _material = null;
        }
    }

    private void OnDestroy()
    {
        ClearVisuals();
    }
}


