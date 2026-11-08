using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using GameZ.Script.Core;
using GameZ.Script.Core.Formats;
using GameZ.Script.Core.Worlds;
using Xunit;

namespace GameZ.Script.Tests;

public sealed class Mw3WorldBackendTests
{
    private static GameZWorld World()
    {
        var world = new GameZWorld { SourceVersion = 27, NodeCapacity = 4, ModelCapacity = 4, MaterialCapacity = 4 };
        var texture = new WorldTexture("surface");
        world.Textures.Add(texture);
        var material = new WorldMaterial { Texture = texture, Mw3Flags = 0x10 };
        material.Mw3Cycle = new() { Loop = true, Speed = 0.5f, Index = 1 };
        material.Mw3Cycle.Textures.Add(texture);
        world.Materials.Add(material);
        var other = new WorldMaterial { Mw3Flags = 0x10, Mw3Cycle = new() { Loop = false, Speed = 1.75f, Index = 1 } };
        other.Mw3Cycle.Textures.Add(texture);
        world.Materials.Add(other);
        var model = new WorldModel { Mw3FacadeMode = 3 };
        model.Vertices.AddRange([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ]);
        model.Polygons.Add(new() { Material = material, Vertices = [0, 1, 2], Uvs = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
            Colors = [new(255, 0, 0), new(0, 255, 0), new(0, 0, 255)], Mw3Field24 = 7 });
        world.Models.Add(model);
        world.Nodes.Add(new("hull", WorldNodeClass.Object3D) { Model = model, Mw3Words = [0, 160, 0, 0], Zone = 255 });
        WorldUpdate.RebuildBounds(world, TestContext.Current.CancellationToken);
        return world;
    }

    [Fact]
    public void NativeWorldKeepsCornerColoursMaterialCyclesAndNodeWords()
    {
        var token = TestContext.Current.CancellationToken;
        var world = World();
        byte[] bytes = GameZWriter.Write(world, token);
        Assert.Equal(27u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        var document = FormatRegistry.Default.OpenBytes("world.zbd", bytes, token: token);
        Assert.DoesNotContain(document.Diagnostics, d => d.Severity == "Error");
        var decoded = GameZWorldReader.FromDocument(document, token);
        Assert.Equal(27u, decoded.SourceVersion);
        Assert.Equal(world.Models[0].Polygons[0].Colors, decoded.Models[0].Polygons[0].Colors);
        Assert.Equal(7u, decoded.Models[0].Polygons[0].Mw3Field24);
        Assert.Equal(0.5f, decoded.Materials[0].Mw3Cycle!.Speed);
        Assert.Equal(1.75f, decoded.Materials[1].Mw3Cycle!.Speed);
        Assert.Equal(world.Nodes[0].Mw3Words, decoded.Nodes[0].Mw3Words);
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, 15, token));
    }

    [Fact]
    public void NativeSourceLoadsIntoVersion27AndAppliesLoadRootOptions()
    {
        var token = TestContext.Current.CancellationToken;
        var world = World();
        var (json, binary) = WorldGltf.Export([world.Nodes[0]], 255, new()
        {
            Game = GameVariant.MechWarrior3,
            Texture = t => ($"../textures/{t.Name}.png", 0)
        }).Write("hull.bin", token);
        Assert.Equal("mw3", WorldGltf.ReadSourceIdentity(json, token).Game);
        Assert.Equal(4, WorldGltf.ReadSourceIdentity(json, token).SourceFormat);
        var files = new MemoryFiles();
        files.Values["data/c1/models/hull.gltf"] = json;
        files.Values["data/c1/models/hull.bin"] = binary;
        files.Values["gamegen/c1.gs"] = Encoding.Latin1.GetBytes("NewWorld world\nSetModelDirectory ..\\data\\c1\\models\nGameGenSetWorld world\nLoadGameGen hull.gltf loaded altitude_off intersect_off\nGameZWriteZBDFile world.zbd\nQuit\n");
        var native = new WorldAssembler(files, token) { Game = GameVariant.MechWarrior3 }.Assemble("c1.gs");
        Assert.Equal(27u, native.SourceVersion);
        Assert.Equal(0u, native.Nodes.Single(n => n.Name == "loaded").Flags & 0x18);
        Assert.Contains(native.Models.SelectMany(m => m.Polygons), p => p.Colors.Length == 3);
        Assert.Throws<InvalidDataException>(() => new WorldAssembler(files, token).Assemble("c1.gs"));
    }

    [Fact]
    public void NativeWorldAndLightPayloadsRetainTheirVersionSpecificLayout()
    {
        var token = TestContext.Current.CancellationToken;
        var world = World();
        var map = new WorldNode("world", WorldNodeClass.World) { Mw3VirtualBounds = [1, 2, 3, 4] };
        var light = new WorldNode("sunlight", WorldNodeClass.Light) { Mw3LightFlags = 64 };
        light.SetPayloadInt(200, 1);
        light.SetPayloadFloat(172, 0.25f); light.SetPayloadFloat(176, 0.5f); light.SetPayloadFloat(180, 1.0f);
        light.SetPayloadFloat(204, 1.0f); light.SetPayloadFloat(208, 10.0f);
        map.WorldLights.Add(light); light.AttachedWorlds.Add(map);
        world.Nodes.Add(map); world.Nodes.Add(light);
        var document = FormatRegistry.Default.OpenBytes("world.zbd", GameZWriter.Write(world, token), token: token);
        Assert.DoesNotContain(document.Diagnostics, d => d.Severity == "Error");
        var decoded = GameZWorldReader.FromDocument(document, token);
        Assert.Equal(map.Mw3VirtualBounds, decoded.Nodes.Single(n => n.Name == "world").Mw3VirtualBounds);
        var sun = decoded.Nodes.Single(n => n.Name == "sunlight");
        Assert.Equal(1, sun.PayloadInt(200));
        Assert.Equal(0.25f, sun.PayloadFloat(172));
        Assert.Equal(64u, sun.Mw3LightFlags!.Value & 64);
        Assert.Single(sun.AttachedWorlds);
    }

    private sealed class MemoryFiles : IProjectFiles
    {
        internal Dictionary<string, byte[]> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string relative) => Values.ContainsKey(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            token.ThrowIfCancellationRequested();
            if (!Values.TryGetValue(relative, out var value)) throw new FileNotFoundException(relative);
            return value;
        }
    }
}
