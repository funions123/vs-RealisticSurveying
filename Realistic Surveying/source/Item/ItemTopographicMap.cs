using System;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace RealisticSurveying;

/// <summary>
/// Stores survey data as flat packed arrays on the item stack.
///
/// Layout:
///   OriginPos : int[3]   — absolute world [X, Y, Z] of the first marker
///   Nodes     : float[]  — packed [dX,dY,dZ, …], one triple per node
///   Edges     : int[]    — packed [a,b, …], one pair per edge (indices into Nodes)
///   Faces     : int[]    — packed [a,b,c, …], one triple per face (auto-detected triangles)
/// </summary>
public class ItemTopographicMap : Item
{
    /// <summary>WatchedAttributes key storing the selected-map slot (-2 = none, -1 = offhand, 0-9 = hotbar).</summary>
    internal const string KeySelectedSlot = "rsSelectedMapSlot";

    public override void OnHeldInteractStart(ItemSlot slot, EntityAgent byEntity, BlockSelection blockSel, EntitySelection entitySel, bool firstEvent, ref EnumHandHandling handling)
    {
        IPlayer? byPlayer = (byEntity as EntityPlayer)?.Player;

        if (blockSel != null && byEntity.World.BlockAccessor.GetBlock(blockSel.Position) is BlockSurveyMarker)
        {
            if (!IsInitialized(slot.Itemstack))
            {
                if (api.Side == EnumAppSide.Server)
                {
                    BlockPos pos = blockSel.Position;

                    if (slot.Itemstack.Item.Code.Path == "blankmap")
                    {
                        // Consume 1 blank map and give the player an initialized topographic map
                        Item topoItem = api.World.GetItem(new AssetLocation("realisticsurveying:topographicmap"));
                        if (topoItem != null)
                        {
                            ItemStack topoStack = new ItemStack(topoItem);
                            topoStack.Attributes["OriginPos"] = new IntArrayAttribute(new[] { pos.X, pos.Y, pos.Z });
                            // Seed the origin marker as node 0 so it is always a valid anchor
                            ((ItemTopographicMap)topoItem).FindOrAddNode(topoStack, Vec3f.Zero);
                            slot.TakeOut(1);
                            slot.MarkDirty();
                            if (byPlayer == null || !byPlayer.InventoryManager.TryGiveItemstack(topoStack))
                                byEntity.World.SpawnItemEntity(topoStack, byEntity.Pos.XYZ);
                        }
                    }
                    else
                    {
                        // topographicmap item used directly (e.g. spawned from creative) — initialize in-place
                        slot.Itemstack.Attributes["OriginPos"] = new IntArrayAttribute(new[] { pos.X, pos.Y, pos.Z });
                        FindOrAddNode(slot.Itemstack, Vec3f.Zero);
                        slot.MarkDirty();
                    }

                    (byPlayer as IServerPlayer)?.SendMessage(
                        GlobalConstants.InfoLogChatGroup,
                        "Map initialized at this survey marker.",
                        EnumChatType.Notification);
                }
                handling = EnumHandHandling.PreventDefault;
            }
            return;
        }

        if ((blockSel == null || byEntity.Controls.Sneak) && IsInitialized(slot.Itemstack))
        {
            if (api.Side == EnumAppSide.Client)
            {
                new GuiDialogSurveyMap((ICoreClientAPI)api, slot.Itemstack).TryOpen();
            }
            else
            {
                // Record which slot holds the selected map so the rope and theodolite can prefer it.
                byEntity.WatchedAttributes.SetInt(KeySelectedSlot, FindSlotIndex(byPlayer, slot));

                // Apply any pending survey data from the registry (handles offline updates on linked copies).
                api.ModLoader.GetModSystem<RealisticSurveyingModSystem>()?.TryApplyRegistryUpdate(slot);
            }
            handling = EnumHandHandling.PreventDefault;
        }
    }

    // ── Internal helper ────────────────────────────────────────────────────

    /// <summary>
    /// Returns the hotbar index (0-9) or -1 (offhand) of the given <paramref name="slot"/>
    /// in the player's inventory, or -2 if not found.
    /// </summary>
    private static int FindSlotIndex(IPlayer? player, ItemSlot slot)
    {
        if (player == null) return -2;
        if (player.InventoryManager.OffhandHotbarSlot == slot) return -1;

        IInventory? hotbar = player.InventoryManager.GetHotbarInventory();
        if (hotbar == null) return -2;

        for (int i = 0; i < Math.Min(10, hotbar.Count); i++)
            if (hotbar[i] == slot) return i;

        return -2;
    }

    // ── Tooltip ────────────────────────────────────────────────────────────

    public override void GetHeldItemInfo(ItemSlot inSlot, StringBuilder dsc, IWorldAccessor world, bool withDebugInfo)
    {
        base.GetHeldItemInfo(inSlot, dsc, world, withDebugInfo);

        if (!IsInitialized(inSlot.Itemstack)) return;

        string name = GetMapName(inSlot.Itemstack);
        dsc.AppendLine(string.IsNullOrWhiteSpace(name) ? "Unnamed Map" : name);

        string linkId = inSlot.Itemstack.Attributes.GetString("LinkId", "");
        if (!string.IsNullOrEmpty(linkId))
        {
            bool isSource = inSlot.Itemstack.Attributes.GetBool("IsLinkSource", false);
            if (isSource)
            {
                dsc.AppendLine(Lang.Get("realisticsurveying:temporallylinked-source"));
            }
            else
            {
                string sourceName = inSlot.Itemstack.Attributes.GetString("LinkSourceName", "");
                dsc.AppendLine(string.IsNullOrEmpty(sourceName)
                    ? Lang.Get("realisticsurveying:temporallylinked-copy")
                    : Lang.Get("realisticsurveying:temporallylinked-copy-named", sourceName));
            }
        }
    }

    // ── Query ──────────────────────────────────────────────────────────────

    public bool IsInitialized(ItemStack stack) =>
        stack.Attributes.HasAttribute("OriginPos");

    public BlockPos GetOrigin(ItemStack stack)
    {
        int[] arr = ((IntArrayAttribute)stack.Attributes["OriginPos"]).value;
        return new BlockPos(arr[0], arr[1], arr[2]);
    }

    /// <summary>Number of survey nodes recorded on this map.</summary>
    public int NodeCount(ItemStack stack) =>
        ((stack.Attributes["Nodes"] as FloatArrayAttribute)?.value?.Length ?? 0) / 3;

    /// <summary>Number of edges recorded on this map.</summary>
    public int EdgeCount(ItemStack stack) =>
        ((stack.Attributes["Edges"] as IntArrayAttribute)?.value?.Length ?? 0) / 2;

    /// <summary>Number of auto-detected triangular faces.</summary>
    public int FaceCount(ItemStack stack) =>
        ((stack.Attributes["Faces"] as IntArrayAttribute)?.value?.Length ?? 0) / 3;

    /// <summary>Returns all edge index pairs as a flat array [a,b, a,b, …]</summary>
    public int[] GetEdges(ItemStack stack) =>
        (stack.Attributes["Edges"] as IntArrayAttribute)?.value ?? Array.Empty<int>();

    /// <summary>Returns all face index triples as a flat array [a,b,c, a,b,c, …]</summary>
    public int[] GetFaces(ItemStack stack) =>
        (stack.Attributes["Faces"] as IntArrayAttribute)?.value ?? Array.Empty<int>();

    /// <summary>World-offset (from map origin) of node at <paramref name="index"/>.</summary>
    public Vec3f GetNode(ItemStack stack, int index)
    {
        float[] v = ((FloatArrayAttribute)stack.Attributes["Nodes"]).value;
        int i = index * 3;
        return new Vec3f(v[i], v[i + 1], v[i + 2]);
    }

    /// <summary>
    /// Returns the raw packed node array [dX,dY,dZ, …]. Intended for hot render loops
    /// that would otherwise call <see cref="GetNode"/> (and re-do the attribute lookup
    /// plus allocate a Vec3f) thousands of times per frame. Do not mutate.
    /// </summary>
    public float[] GetNodesRaw(ItemStack stack) =>
        (stack.Attributes["Nodes"] as FloatArrayAttribute)?.value ?? Array.Empty<float>();

    /// <summary>
    /// Returns true if a node within <paramref name="epsilon"/> of <paramref name="offset"/>
    /// already exists in the map. Use this to enforce connectivity before adding new nodes.
    /// </summary>
    public bool NodeExists(ItemStack stack, Vec3f offset, float epsilon = 0.5f)
    {
        int count = NodeCount(stack);
        for (int i = 0; i < count; i++)
        {
            Vec3f n = GetNode(stack, i);
            if (Math.Abs(n.X - offset.X) < epsilon &&
                Math.Abs(n.Y - offset.Y) < epsilon &&
                Math.Abs(n.Z - offset.Z) < epsilon)
                return true;
        }
        return false;
    }

    /// <summary>Returns the custom label for node at <paramref name="index"/>, or empty string if none.</summary>
    public string GetNodeLabel(ItemStack stack, int index) =>
        stack.Attributes.GetString($"NodeLabel_{index}", "");

    /// <summary>Returns the player-assigned name of this map, or empty string if unnamed.</summary>
    public string GetMapName(ItemStack stack) =>
        stack.Attributes.GetString("MapName", "");

    /// <summary>Sets the map name. Pass null or empty to clear.</summary>
    public void SetMapName(ItemStack stack, string name)
    {
        if (string.IsNullOrEmpty(name))
            stack.Attributes.RemoveAttribute("MapName");
        else
            stack.Attributes.SetString("MapName", name);
    }

    public bool HasEdge(ItemStack stack, int a, int b)
    {
        if (stack.Attributes["Edges"] is not IntArrayAttribute attr) return false;
        int[] v = attr.value;
        for (int i = 0; i < v.Length; i += 2)
            if (v[i] == a && v[i + 1] == b || v[i] == b && v[i + 1] == a)
                return true;
        return false;
    }

    public bool HasFace(ItemStack stack, int a, int b, int c)
    {
        if (stack.Attributes["Faces"] is not IntArrayAttribute attr) return false;
        int[] v = attr.value;
        for (int i = 0; i < v.Length; i += 3)
        {
            int p = v[i], q = v[i + 1], r = v[i + 2];
            // Same triangle regardless of winding
            if ((p == a || p == b || p == c) &&
                (q == a || q == b || q == c) &&
                (r == a || r == b || r == c))
                return true;
        }
        return false;
    }

    // ── Mutation ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the index of an existing node within <paramref name="epsilon"/> of
    /// <paramref name="offset"/>, or appends a new node and returns its index.
    /// </summary>
    public int FindOrAddNode(ItemStack stack, Vec3f offset, float epsilon = 0.01f)
    {
        int count = NodeCount(stack);
        for (int i = 0; i < count; i++)
        {
            Vec3f n = GetNode(stack, i);
            if (Math.Abs(n.X - offset.X) < epsilon &&
                Math.Abs(n.Y - offset.Y) < epsilon &&
                Math.Abs(n.Z - offset.Z) < epsilon)
                return i;
        }

        FloatArrayAttribute nodes = GetOrCreateFloats(stack, "Nodes");
        nodes.value = nodes.value.Append(offset.X, offset.Y, offset.Z);
        return count;
    }

    /// <summary>
    /// Records a measurement edge between nodes <paramref name="a"/> and <paramref name="b"/>.
    /// Silently no-ops if this edge already exists.
    /// After writing the edge, checks whether it completes any triangle and records the face.
    /// </summary>
    public void AddEdgeAndDetectFace(ItemStack stack, int a, int b)
    {
        if (HasEdge(stack, a, b)) return;

        IntArrayAttribute edges = GetOrCreateInts(stack, "Edges");
        edges.value = edges.value.Append(a, b);

        DetectAndAddFace(stack, a, b);
    }

    /// <summary>
    /// Removes the node at <paramref name="nodeIndex"/>, along with all edges and faces that
    /// reference it.  Remaining edge/face indices are remapped and node labels are shifted down.
    /// </summary>
    public void DeleteNode(ItemStack stack, int nodeIndex)
    {
        int nodeCount = NodeCount(stack);
        if (nodeIndex < 0 || nodeIndex >= nodeCount) return;

        // ── Remove node from float array ───────────────────────────────────────
        float[] nodes = ((FloatArrayAttribute)stack.Attributes["Nodes"]).value;
        float[] newNodes = new float[(nodeCount - 1) * 3];
        for (int i = 0, di = 0; i < nodeCount; i++)
        {
            if (i == nodeIndex) continue;
            newNodes[di++] = nodes[i * 3];
            newNodes[di++] = nodes[i * 3 + 1];
            newNodes[di++] = nodes[i * 3 + 2];
        }
        if (newNodes.Length == 0)
            stack.Attributes.RemoveAttribute("Nodes");
        else
            stack.Attributes["Nodes"] = new FloatArrayAttribute(newNodes);

        // ── Rebuild edges (remove any that touch the deleted node, remap the rest) ──
        int[] edges = GetEdges(stack);
        List<int> newEdges = new List<int>();
        for (int i = 0; i < edges.Length; i += 2)
        {
            int a = edges[i], b = edges[i + 1];
            if (a == nodeIndex || b == nodeIndex) continue;
            newEdges.Add(a > nodeIndex ? a - 1 : a);
            newEdges.Add(b > nodeIndex ? b - 1 : b);
        }
        if (newEdges.Count == 0)
            stack.Attributes.RemoveAttribute("Edges");
        else
            stack.Attributes["Edges"] = new IntArrayAttribute(newEdges.ToArray());

        // ── Rebuild faces ──────────────────────────────────────────────────────
        int[] faces = GetFaces(stack);
        List<int> newFaces = new List<int>();
        for (int i = 0; i < faces.Length; i += 3)
        {
            int a = faces[i], b = faces[i + 1], c = faces[i + 2];
            if (a == nodeIndex || b == nodeIndex || c == nodeIndex) continue;
            newFaces.Add(a > nodeIndex ? a - 1 : a);
            newFaces.Add(b > nodeIndex ? b - 1 : b);
            newFaces.Add(c > nodeIndex ? c - 1 : c);
        }
        if (newFaces.Count == 0)
            stack.Attributes.RemoveAttribute("Faces");
        else
            stack.Attributes["Faces"] = new IntArrayAttribute(newFaces.ToArray());

        // ── Shift node labels down ─────────────────────────────────────────────
        for (int i = nodeIndex; i < nodeCount - 1; i++)
        {
            string val = stack.Attributes.GetString($"NodeLabel_{i + 1}", "");
            if (!string.IsNullOrEmpty(val))
                stack.Attributes.SetString($"NodeLabel_{i}", val);
            else
                stack.Attributes.RemoveAttribute($"NodeLabel_{i}");
            stack.Attributes.RemoveAttribute($"NodeLabel_{i + 1}");
        }
    }

    /// <summary>Sets a custom label on the node at <paramref name="index"/>. Pass empty/null to clear.</summary>
    public void SetNodeLabel(ItemStack stack, int index, string label)
    {
        if (string.IsNullOrEmpty(label))
            stack.Attributes.RemoveAttribute($"NodeLabel_{index}");
        else
            stack.Attributes.SetString($"NodeLabel_{index}", label);
    }

    // ── Stroke data ────────────────────────────────────────────────────────

    /// <summary>Number of annotation strokes recorded on this map.</summary>
    public int StrokeCount(ItemStack stack) =>
        stack.Attributes.GetInt("StrokeCount", 0);

    /// <summary>Appends a new ink stroke to the map item stack.</summary>
    public void AddStroke(ItemStack stack, int colorIndex, float width, float[] points)
    {
        int n = StrokeCount(stack);
        stack.Attributes.SetInt($"Stroke_{n}_Color", colorIndex);
        stack.Attributes.SetFloat($"Stroke_{n}_Width", width);
        stack.Attributes[$"Stroke_{n}_Pts"] = new FloatArrayAttribute(points);
        stack.Attributes.SetInt("StrokeCount", n + 1);
    }

    /// <summary>Removes the most-recently added stroke, if any.</summary>
    public void RemoveLastStroke(ItemStack stack)
    {
        int n = StrokeCount(stack);
        if (n <= 0) return;
        n--;
        stack.Attributes.RemoveAttribute($"Stroke_{n}_Color");
        stack.Attributes.RemoveAttribute($"Stroke_{n}_Width");
        stack.Attributes.RemoveAttribute($"Stroke_{n}_Pts");
        stack.Attributes.SetInt("StrokeCount", n);
    }

    /// <summary>Returns the color index, line width, and flat dX/dZ point array for the stroke at <paramref name="index"/>.</summary>
    public (int colorIdx, float width, float[] pts) GetStroke(ItemStack stack, int index)
    {
        int   c   = stack.Attributes.GetInt($"Stroke_{index}_Color", 3);
        float w   = stack.Attributes.GetFloat($"Stroke_{index}_Width", 1.5f);
        float[] pts = (stack.Attributes[$"Stroke_{index}_Pts"] as FloatArrayAttribute)?.value
                      ?? Array.Empty<float>();
        return (c, w, pts);
    }

    // ── Temporal linking ───────────────────────────────────────────────────

    /// <summary>
    /// When a topographic map is produced by the linked-map recipe, assigns a shared <c>LinkId</c> to 
    /// both the source map and the output copy so the server can propagate future survey data from source to copies.
    /// </summary>
    public override void OnCreatedByCrafting(ItemSlot[] allInputSlots, ItemSlot outputSlot, IRecipeBase byRecipe)
    {
        base.OnCreatedByCrafting(allInputSlots, outputSlot, byRecipe);

        if (api?.Side != EnumAppSide.Server) return;

        // Activate the linking process when the recipe is used, identified by the presence of a map and temporal gear
        bool hasTemporalGear = false;
        ItemSlot? sourceMapSlot = null;
        foreach (ItemSlot s in allInputSlots)
        {
            if (s?.Itemstack == null) continue;
            AssetLocation code = s.Itemstack.Item?.Code;
            if (code?.Domain == "game" && code.Path == "gear-temporal")
                hasTemporalGear = true;
            else if (s.Itemstack.Item is ItemTopographicMap m && m.IsInitialized(s.Itemstack))
                sourceMapSlot = s;
        }

        if (!hasTemporalGear || sourceMapSlot == null) return;

        // Generate or reuse the link ID
        string linkId = sourceMapSlot.Itemstack.Attributes.GetString("LinkId", "");
        if (string.IsNullOrEmpty(linkId))
        {
            linkId = Guid.NewGuid().ToString("N"); // use GUID
            sourceMapSlot.Itemstack.Attributes.SetString("LinkId", linkId);
            sourceMapSlot.Itemstack.Attributes.SetBool("IsLinkSource", true);
            sourceMapSlot.MarkDirty();
        }

        // Apply to the newly crafted copy
        outputSlot.Itemstack.Attributes.SetString("LinkId", linkId);
        outputSlot.Itemstack.Attributes.RemoveAttribute("IsLinkSource"); // copies are never the source

        // Store the source map's name so the copy can display it in its tooltip
        string sourceName = sourceMapSlot.Itemstack.Attributes.GetString("MapName", "");
        if (!string.IsNullOrEmpty(sourceName))
            outputSlot.Itemstack.Attributes.SetString("LinkSourceName", sourceName);
        else
            outputSlot.Itemstack.Attributes.RemoveAttribute("LinkSourceName");
    }

    /// <summary>
    /// Called after survey data is added to <paramref name="sourceStack"/>.
    /// Snapshots the current topology into the server registry so any linked copy
    /// receives it the next time it is opened.
    /// </summary>
    public static void PropagateToLinkedMaps(ItemStack sourceStack, ICoreServerAPI sapi)
    {
        string linkId = sourceStack.Attributes.GetString("LinkId", "");
        if (string.IsNullOrEmpty(linkId)) return;

        sapi.ModLoader.GetModSystem<RealisticSurveyingModSystem>()
            ?.UpdateLinkRegistry(linkId, sourceStack);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Searches all nodes c ≠ a,b for one where edges (a,c) and (b,c) both exist,
    /// completing a triangle with the newly added edge (a,b).
    /// </summary>
    private void DetectAndAddFace(ItemStack stack, int a, int b)
    {
        if (stack.Attributes["Edges"] is not IntArrayAttribute edgesAttr) return;
        int[] edges = edgesAttr.value;
        int nodeCount = NodeCount(stack);

        for (int c = 0; c < nodeCount; c++)
        {
            if (c == a || c == b) continue;

            bool hasAC = false, hasBC = false;
            for (int i = 0; i < edges.Length; i += 2)
            {
                int u = edges[i], w = edges[i + 1];
                if (u == a && w == c || u == c && w == a) hasAC = true;
                if (u == b && w == c || u == c && w == b) hasBC = true;
                if (hasAC && hasBC) break;
            }

            if (hasAC && hasBC && !HasFace(stack, a, b, c))
            {
                IntArrayAttribute faces = GetOrCreateInts(stack, "Faces");
                faces.value = faces.value.Append(a, b, c);
            }
        }
    }

    private static FloatArrayAttribute GetOrCreateFloats(ItemStack stack, string key)
    {
        if (stack.Attributes[key] is FloatArrayAttribute existing) return existing;
        FloatArrayAttribute created = new FloatArrayAttribute(Array.Empty<float>());
        stack.Attributes[key] = created;
        return created;
    }

    private static IntArrayAttribute GetOrCreateInts(ItemStack stack, string key)
    {
        if (stack.Attributes[key] is IntArrayAttribute existing) return existing;
        IntArrayAttribute created = new IntArrayAttribute(Array.Empty<int>());
        stack.Attributes[key] = created;
        return created;
    }
}
