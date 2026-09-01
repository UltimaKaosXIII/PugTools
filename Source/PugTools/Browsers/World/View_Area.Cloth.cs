using System;
using System.Collections.Generic;
using System.Linq;
using FileFormats;
using SlimDX;

namespace PugTools {
  internal sealed partial class View_AREA {
    // Port of SWTOR/Jedipedia's bwa::ClothInstance core. CLO data is shared per appearance part; these compiled
    // arrays are immutable. The verlet positions below are kept per WorldNpcPlacement so identical capes can move
    // independently on different actors.
    private sealed class NpcClothCompiled {
      public WorldNpcClothAsset Source;
      public bool Rigid;
      public int ParticleCount;
      public string[] BoneNames;
      public readonly Dictionary<string, int> BoneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
      public string[] ParentNames;
      public int[] ParentIndex;
      public int[] Tip;
      public bool[] TipSkinned;
      public float[] RestPos;
      public float[] SimWeight;
      public float[] Damping;
      public float[] MoveFactor;
      public float[] ParticleRadius;
      public int[] Movable;
      public int[] Anchored;
      public int[] EdgeN1, EdgeN2;
      public float[] EdgeRest, EdgeMax, EdgeMin, EdgeK, EdgeSpring1, EdgeSpring2, EdgeClamp1, EdgeClamp2;
      public Vector3 Gravity;
      public int ColliderCount;
      public int[] ColClass, ColParent;
      public float[] ColRadius, ColHeight, ColFriction;
      public Matrix[] ColLocal;
      public int[] ParticleMask;
    }

    private sealed class NpcClothState {
      public NpcClothCompiled Asset;
      public float[] Pos, OldPos, Accel, Target, TargetPrev;
      public Matrix[] BoneMatrices, AnchorMatrix;
      public Quaternion[] AnchorQuat;
      public Matrix[] ColliderBind, ColliderWorld, ColliderPrev;
      public float[] ColliderPrim;
      public int[] ContactBits, PrevContacts;
      public bool ColliderPlaced;
      public bool ColliderBindResolved;
      public float Accumulator;
      public int RestCounter;
      public bool AnchorsDirty = true;
      public bool Seeded;
      public float LastElapsed = Single.NaN;
    }

    private readonly Dictionary<WorldNpcClothAsset, NpcClothCompiled> npcClothCompiled = new Dictionary<WorldNpcClothAsset, NpcClothCompiled>();
    private readonly Dictionary<WorldNpcPlacement, Dictionary<WorldNpcClothAsset, NpcClothState>> npcClothStates = new Dictionary<WorldNpcPlacement, Dictionary<WorldNpcClothAsset, NpcClothState>>();

    private const float NpcClothDtNear = 1f / 64f;
    private const int NpcClothIterationsNear = 5;
    private const float NpcClothDtFar = 1f / 32f;
    private const int NpcClothIterationsFar = 10;
    private const float NpcClothMinScreenPixels = 24f;
    private const float NpcClothNearScreenPixels = 96f;
    private const int NpcClothMaxSubsteps = 4;
    private const float NpcClothStrainCap = 1f;
    private const float NpcClothTeleportDistanceSq = .4f * .4f;
    private const float NpcClothMotionScale = 4f;
    private const float NpcClothFrictionDrag = 10f;
    private const float NpcClothRestMotionSq = 1e-12f;
    private const int NpcClothRestSubsteps = 3;
    private const float NpcClothDeadzoneCos = .9999f;

    private static string ClothBoneName(string name) => NpcCanonicalAnimationBoneName(name ?? String.Empty);

    private NpcClothCompiled CompileNpcCloth(WorldNpcClothAsset source) {
      if (source?.Cloth == null || source.Model == null) return null;
      if (npcClothCompiled.TryGetValue(source, out NpcClothCompiled cached)) return cached;
      ViewCLO.CloInfo parsed = source.Cloth;
      int count = parsed.Particles?.Count ?? 0;
      if (count <= 0 || parsed.Bones == null || parsed.Bones.Count != count) { npcClothCompiled[source] = null; return null; }

      var cloth = new NpcClothCompiled {
        Source = source, ParticleCount = count,
        BoneNames = new string[count], ParentIndex = new int[count], Tip = new int[count], TipSkinned = new bool[count],
        RestPos = new float[count * 3], SimWeight = new float[count], Damping = new float[count], MoveFactor = new float[count],
        ParticleRadius = new float[count], Gravity = new Vector3(parsed.GravityX, parsed.GravityY, parsed.GravityZ)
      };
      var parents = new List<string>();
      int movableCount = 0;
      for (int i = 0; i < count; i++) {
        ViewCLO.CloBone bone = parsed.Bones[i];
        ViewCLO.CloParticle particle = parsed.Particles[i];
        if (bone == null || particle == null) { npcClothCompiled[source] = null; return null; }
        string boneName = ClothBoneName(bone.Name);
        cloth.BoneNames[i] = boneName;
        if (!String.IsNullOrWhiteSpace(boneName)) cloth.BoneByName[boneName] = i;

        Quaternion inverseBindRotation = new Quaternion(-bone.RootToBoneRot[0], -bone.RootToBoneRot[1], -bone.RootToBoneRot[2], bone.RootToBoneRot[3]);
        Matrix invRot = NpcJedipediaRotationMatrix(inverseBindRotation);
        Vector3 inverseBindTranslation = new Vector3(bone.RootToBoneTrans[0], bone.RootToBoneTrans[1], bone.RootToBoneTrans[2]);
        Vector3 rest = ClothTransformNormal(inverseBindTranslation, invRot);
        cloth.RestPos[i * 3] = -rest.X; cloth.RestPos[i * 3 + 1] = -rest.Y; cloth.RestPos[i * 3 + 2] = -rest.Z;

        float weight = particle.InvertedSimWeight;
        cloth.SimWeight[i] = weight;
        cloth.Damping[i] = particle.Damping;
        cloth.MoveFactor[i] = particle.MovementForceFactor;
        cloth.ParticleRadius[i] = particle.Radius ?? 0f;
        cloth.Tip[i] = bone.StartParticle >= 0 && bone.EndParticle >= 0 && bone.EndParticle != i && bone.EndParticle < count ? bone.EndParticle : -1;
        if (weight < 1f) movableCount++;

        string parent = ClothBoneName(bone.Parent);
        int parentSlot = parents.FindIndex(x => String.Equals(x, parent, StringComparison.OrdinalIgnoreCase));
        if (parentSlot < 0) { parentSlot = parents.Count; parents.Add(parent); }
        cloth.ParentIndex[i] = parentSlot;
      }
      cloth.ParentNames = parents.ToArray();

      var n1 = new List<int>(); var n2 = new List<int>(); var restLength = new List<float>(); var maxLength = new List<float>();
      var minLength = new List<float>(); var k = new List<float>(); var spring1 = new List<float>(); var spring2 = new List<float>();
      var clamp1 = new List<float>(); var clamp2 = new List<float>();
      foreach (ViewCLO.CloEdge edge in parsed.Edges) {
        int movement = unchecked((int)edge.Movement);
        if (movement != 0 && movement != 2 && movement != 4) continue;
        if (edge.Node1 >= (uint)count || edge.Node2 >= (uint)count) { npcClothCompiled[source] = null; return null; }
        n1.Add((int)edge.Node1); n2.Add((int)edge.Node2); restLength.Add(edge.RestLength);
        maxLength.Add(edge.MaxLength > 0f ? edge.MaxLength : edge.RestLength * 1.25f);
        minLength.Add(edge.MinLength.HasValue && edge.MinLength.Value > 0f ? edge.MinLength.Value : 0f);
        k.Add(edge.SpringStrength);
        spring1.Add(movement == 0 ? 0f : 1f); spring2.Add(movement == 4 ? 0f : -1f);
        clamp1.Add(movement == 0 ? 0f : movement == 2 ? .5f : 1f); clamp2.Add(movement == 4 ? 0f : movement == 2 ? -.5f : -1f);
      }
      cloth.EdgeN1 = n1.ToArray(); cloth.EdgeN2 = n2.ToArray(); cloth.EdgeRest = restLength.ToArray(); cloth.EdgeMax = maxLength.ToArray();
      cloth.EdgeMin = minLength.ToArray(); cloth.EdgeK = k.ToArray(); cloth.EdgeSpring1 = spring1.ToArray(); cloth.EdgeSpring2 = spring2.ToArray();
      cloth.EdgeClamp1 = clamp1.ToArray(); cloth.EdgeClamp2 = clamp2.ToArray();

      cloth.Rigid = movableCount == 0 || cloth.EdgeN1.Length == 0;
      if (cloth.Rigid) { for (int i = 0; i < count; i++) cloth.SimWeight[i] = 1f; movableCount = 0; }
      var movable = new List<int>(movableCount); var anchored = new List<int>();
      for (int i = 0; i < count; i++) { if (cloth.SimWeight[i] < 1f) movable.Add(i); if (cloth.SimWeight[i] > 0f) anchored.Add(i); }
      cloth.Movable = movable.ToArray(); cloth.Anchored = anchored.ToArray();

      var skinnedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
      if (source.Model.meshes != null) foreach (GR2_Mesh mesh in source.Model.meshes)
        if (mesh?.meshBones != null) foreach (GR2_Mesh_Bone meshBone in mesh.meshBones) skinnedNames.Add(ClothBoneName(meshBone?.boneName));
      for (int i = 0; i < count; i++) cloth.TipSkinned[i] = cloth.Tip[i] >= 0 && skinnedNames.Contains(cloth.BoneNames[cloth.Tip[i]]);

      CompileNpcClothColliders(cloth, parsed, parents);
      npcClothCompiled[source] = cloth;
      return cloth;
    }

    private static void CompileNpcClothColliders(NpcClothCompiled cloth, ViewCLO.CloInfo parsed, List<string> parents) {
      int colliderCount = Math.Min(parsed.Colliders?.Count ?? 0, 31);
      cloth.ColliderCount = colliderCount;
      if (colliderCount <= 0) return;
      cloth.ColClass = new int[colliderCount]; cloth.ColParent = new int[colliderCount]; cloth.ColRadius = new float[colliderCount];
      cloth.ColHeight = new float[colliderCount]; cloth.ColFriction = new float[colliderCount]; cloth.ColLocal = new Matrix[colliderCount];
      for (int c = 0; c < colliderCount; c++) {
        ViewCLO.CloCollider collider = parsed.Colliders[c];
        cloth.ColClass[c] = unchecked((int)collider.Class); cloth.ColRadius[c] = collider.Radius; cloth.ColHeight[c] = collider.Height; cloth.ColFriction[c] = collider.Friction;
        string name = ClothBoneName(collider.Parent);
        int slot = parents.FindIndex(x => String.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        if (slot < 0) { slot = parents.Count; parents.Add(name); }
        cloth.ColParent[c] = slot;
        Quaternion rotation = new Quaternion(collider.Rotation[0], collider.Rotation[1], collider.Rotation[2], collider.Rotation[3]);
        Matrix local = NpcJedipediaRotationMatrix(rotation);
        local.M41 = collider.Position[0]; local.M42 = collider.Position[1]; local.M43 = collider.Position[2]; local.M44 = 1f;
        cloth.ColLocal[c] = local;
      }
      cloth.ParentNames = parents.ToArray();
      cloth.ParticleMask = new int[cloth.ParticleCount];
      for (int i = 0; i < cloth.ParticleCount; i++)
        cloth.ParticleMask[i] = cloth.SimWeight[i] >= 1f ? 0 : unchecked((int)parsed.Particles[i].ColliderBitflag);
    }

    private NpcClothState GetNpcClothState(WorldNpcPlacement placement, WorldNpcClothAsset source) {
      if (placement == null || source == null) return null;
      if (!npcClothStates.TryGetValue(placement, out Dictionary<WorldNpcClothAsset, NpcClothState> byAsset))
        npcClothStates[placement] = byAsset = new Dictionary<WorldNpcClothAsset, NpcClothState>();
      if (byAsset.TryGetValue(source, out NpcClothState state)) return state;
      NpcClothCompiled cloth = CompileNpcCloth(source);
      if (cloth == null) { byAsset[source] = null; return null; }
      int count = cloth.ParticleCount;
      state = new NpcClothState {
        Asset = cloth, Pos = new float[count * 3], OldPos = new float[count * 3], Accel = new float[count * 3], Target = new float[count * 3], TargetPrev = new float[count * 3],
        BoneMatrices = new Matrix[count], AnchorMatrix = Enumerable.Repeat(Matrix.Identity, cloth.ParentNames.Length).ToArray(), AnchorQuat = new Quaternion[cloth.ParentNames.Length]
      };
      if (cloth.ColliderCount > 0) {
        state.ColliderBind = new Matrix[cloth.ColliderCount]; state.ColliderWorld = new Matrix[cloth.ColliderCount]; state.ColliderPrev = new Matrix[cloth.ColliderCount];
        state.ColliderPrim = new float[cloth.ColliderCount * 8]; state.ContactBits = new int[count]; state.PrevContacts = new int[count];
      }
      byAsset[source] = state;
      return state;
    }

    private void ApplyNpcCloth(NpcSkinState skin, WorldNpcPlacement placement, GR2 model, Matrix world, WorldRenderSettings settings) {
      if (skin == null || placement?.ClothAssets == null || model == null || placement.ClothAssets.Count == 0) return;
      foreach (WorldNpcClothAsset source in placement.ClothAssets) {
        if (source == null || !ReferenceEquals(source.Model, model)) continue;
        NpcClothState state = GetNpcClothState(placement, source);
        if (state == null) continue;
        CaptureNpcClothAnchors(state, skin);
        float frameDt = Single.IsFinite(state.LastElapsed) ? Math.Max(0f, Math.Min(.25f, elapsed - state.LastElapsed)) : 1f / 60f;
        state.LastElapsed = elapsed;
        float screenPixels = NpcClothScreenPixels(model, world, settings);
        bool simulate = screenPixels >= NpcClothMinScreenPixels && !state.Asset.Rigid;
        if (!simulate) ClothHold(state);
        else if (!state.Seeded) ClothSeed(state);
        else {
          bool near = screenPixels >= NpcClothNearScreenPixels;
          ClothStep(state, frameDt, near ? NpcClothDtNear : NpcClothDtFar, near ? NpcClothIterationsNear : NpcClothIterationsFar);
        }
        ClothWriteBones(state);
        foreach (KeyValuePair<GR2_Mesh, Matrix[]> pair in skin.Palettes) {
          GR2_Mesh mesh = pair.Key; Matrix[] palette = pair.Value;
          if (mesh?.meshBones == null || palette == null) continue;
          for (int i = 0; i < palette.Length && i < mesh.meshBones.Count; i++) {
            string name = ClothBoneName(mesh.meshBones[i]?.boneName);
            if (state.Asset.BoneByName.TryGetValue(name, out int clothBone)) palette[i] = state.BoneMatrices[clothBone];
          }
        }
      }
    }

    private float NpcClothScreenPixels(GR2 model, Matrix world, WorldRenderSettings settings) {
      if (camera == null || model == null || settings == null || !TryModelSphere(model, world, out Vector3 center, out float radius)) return NpcClothNearScreenPixels;
      float distance = (center - camera.Position).Length();
      if (!(distance > .001f) || !(radius > 0f)) return NpcClothNearScreenPixels;
      float scale = Math.Max(1f, Viewport.Height) / (2f * (float)Math.Tan(GetFieldOfViewRadians(settings) * .5f));
      return Math.Max(0f, (2f * radius / distance) * scale);
    }

    private static Matrix NpcClothBindWorld(IList<GR2_Bone_Skeleton> skeleton, string name) {
      if (skeleton != null) for (int i = 0; i < skeleton.Count; i++)
        if (String.Equals(ClothBoneName(skeleton[i]?.boneName), name, StringComparison.OrdinalIgnoreCase)) return skeleton[i].root;
      return Matrix.Identity;
    }

    private void CaptureNpcClothAnchors(NpcClothState state, NpcSkinState skin) {
      NpcClothCompiled cloth = state.Asset;
      for (int p = 0; p < cloth.ParentNames.Length; p++) {
        string name = cloth.ParentNames[p];
        state.AnchorMatrix[p] = skin.SkinMatrices.TryGetValue(name, out Matrix matrix) ? matrix : Matrix.Identity;
      }
      state.AnchorsDirty = true;
      if (cloth.ColliderCount <= 0) return;
      if (!state.ColliderBindResolved) {
        for (int c = 0; c < cloth.ColliderCount; c++) {
          Matrix bindWorld = NpcClothBindWorld(skin.Skeleton, cloth.ParentNames[cloth.ColParent[c]]);
          state.ColliderBind[c] = cloth.ColLocal[c] * bindWorld;
        }
        state.ColliderBindResolved = true;
      }
      ClothPlaceColliders(state);
    }

    private static Vector3 ClothTransformPoint(Vector3 v, Matrix m) => new Vector3(
      v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41,
      v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42,
      v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43);
    private static Vector3 ClothTransformNormal(Vector3 v, Matrix m) => new Vector3(
      v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31,
      v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32,
      v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33);

    private static Quaternion ClothQuatMultiply(Quaternion a, Quaternion b) => new Quaternion(
      a.X*b.W+a.W*b.X+a.Y*b.Z-a.Z*b.Y,
      a.Y*b.W+a.W*b.Y+a.Z*b.X-a.X*b.Z,
      a.Z*b.W+a.W*b.Z+a.X*b.Y-a.Y*b.X,
      a.W*b.W-a.X*b.X-a.Y*b.Y-a.Z*b.Z);

    private static Quaternion ClothQuatNormalize(Quaternion q) {
      float length = (float)Math.Sqrt(q.X*q.X + q.Y*q.Y + q.Z*q.Z + q.W*q.W);
      if (!(length > 1e-9f) || !Single.IsFinite(length)) return Quaternion.Identity;
      float inv = 1f / length; return new Quaternion(q.X*inv, q.Y*inv, q.Z*inv, q.W*inv);
    }

    private static Quaternion ClothQuatAxisAngle(Vector3 axis, float angle) {
      float len = axis.Length(); if (!(len > 1e-9f)) return Quaternion.Identity; axis /= len;
      float half = angle * .5f, sin = (float)Math.Sin(half);
      return new Quaternion(axis.X*sin, axis.Y*sin, axis.Z*sin, (float)Math.Cos(half));
    }

    private static Quaternion ClothRotationTo(Vector3 from, Vector3 to) {
      float fromLen = from.Length(), toLen = to.Length();
      if (!(fromLen > 1e-7f) || !(toLen > 1e-7f)) return Quaternion.Identity;
      from /= fromLen; to /= toLen;
      float dot = Math.Max(-1f, Math.Min(1f, Vector3.Dot(from, to)));
      if (dot > .999999f) return Quaternion.Identity;
      if (dot < -.999999f) {
        Vector3 axis = Vector3.Cross(from, Math.Abs(from.X) < .7f ? Vector3.UnitX : Vector3.UnitY);
        if (axis.LengthSquared() < 1e-8f) axis = Vector3.UnitZ; else axis.Normalize();
        return ClothQuatAxisAngle(axis, (float)Math.PI);
      }
      Vector3 cross = Vector3.Cross(from, to);
      float s = (float)Math.Sqrt((1f + dot) * 2f), inv = 1f / s;
      Quaternion q = new Quaternion(cross.X * inv, cross.Y * inv, cross.Z * inv, s * .5f);
      return ClothQuatNormalize(q);
    }

    private static void ClothComputeTargets(NpcClothState state) {
      NpcClothCompiled cloth = state.Asset;
      for (int i = 0; i < cloth.ParticleCount; i++) {
        int k = i * 3;
        Vector3 rest = new Vector3(cloth.RestPos[k], cloth.RestPos[k + 1], cloth.RestPos[k + 2]);
        Vector3 target = ClothTransformPoint(rest, state.AnchorMatrix[cloth.ParentIndex[i]]);
        state.Target[k] = target.X; state.Target[k + 1] = target.Y; state.Target[k + 2] = target.Z;
      }
    }

    private static void ClothSeed(NpcClothState state) {
      ClothComputeTargets(state);
      Array.Copy(state.Target, state.Pos, state.Pos.Length); Array.Copy(state.Target, state.OldPos, state.Pos.Length); Array.Copy(state.Target, state.TargetPrev, state.Pos.Length);
      Array.Clear(state.Accel, 0, state.Accel.Length); state.Accumulator = 0f; state.RestCounter = 0;
      if (state.ContactBits != null) { Array.Clear(state.ContactBits, 0, state.ContactBits.Length); Array.Clear(state.PrevContacts, 0, state.PrevContacts.Length); }
      state.Seeded = true;
    }

    private static void ClothHold(NpcClothState state) {
      if (!state.AnchorsDirty && state.Seeded) return;
      state.AnchorsDirty = false; ClothComputeTargets(state);
      Array.Copy(state.Target, state.Pos, state.Pos.Length); Array.Copy(state.Target, state.OldPos, state.Pos.Length); Array.Copy(state.Target, state.TargetPrev, state.Pos.Length);
      state.Accumulator = 0f; state.Seeded = true;
    }

    private static void ClothStep(NpcClothState state, float frameDt, float fixedDt, int iterations) {
      NpcClothCompiled cloth = state.Asset;
      Array.Copy(state.Target, state.TargetPrev, state.Target.Length); ClothComputeTargets(state);
      float dx = state.Target[0] - state.TargetPrev[0], dy = state.Target[1] - state.TargetPrev[1], dz = state.Target[2] - state.TargetPrev[2];
      if (dx*dx + dy*dy + dz*dz > NpcClothTeleportDistanceSq) { ClothSeed(state); return; }
      if (state.AnchorsDirty) state.RestCounter = 0;
      state.AnchorsDirty = false;
      if (state.RestCounter >= NpcClothRestSubsteps) return;
      state.Accumulator = Math.Min(state.Accumulator + frameDt, NpcClothMaxSubsteps * fixedDt);
      int steps = (int)Math.Floor(state.Accumulator / fixedDt);
      if (steps <= 0) return;
      state.Accumulator -= steps * fixedDt;
      float motionScale = NpcClothMotionScale / (fixedDt * steps);
      for (int step = 0; step < steps; step++) {
        ClothForces(state, motionScale, fixedDt); ClothIntegrate(state, fixedDt); ClothAnchor(state); ClothRelax(state, iterations);
        if (cloth.ColliderCount > 0) ClothCollide(state);
        state.RestCounter = ClothSubstepMotion(state) < NpcClothRestMotionSq ? state.RestCounter + 1 : 0;
      }
      if (!Single.IsFinite(state.Pos[0]) || Math.Abs(state.Pos[0]) > 1e6f) ClothSeed(state);
    }

    private static void ClothForces(NpcClothState state, float motionScale, float dt) {
      NpcClothCompiled cloth = state.Asset;
      for (int i = 0; i < cloth.ParticleCount; i++) {
        int k = i * 3; float scale = cloth.MoveFactor[i] * motionScale;
        state.Accel[k] = cloth.Gravity.X + (state.Target[k] - state.TargetPrev[k]) * scale;
        state.Accel[k + 1] = cloth.Gravity.Y + (state.Target[k + 1] - state.TargetPrev[k + 1]) * scale;
        state.Accel[k + 2] = cloth.Gravity.Z + (state.Target[k + 2] - state.TargetPrev[k + 2]) * scale;
      }
      for (int e = 0; e < cloth.EdgeN1.Length; e++) {
        int a = cloth.EdgeN1[e] * 3, b = cloth.EdgeN2[e] * 3;
        float ex = state.Pos[b] - state.Pos[a], ey = state.Pos[b + 1] - state.Pos[a + 1], ez = state.Pos[b + 2] - state.Pos[a + 2];
        float len = (float)Math.Sqrt(ex*ex + ey*ey + ez*ez); if (!(len > 1e-9f)) len = 1e-9f;
        float stretch = Math.Min(NpcClothStrainCap, len) - cloth.EdgeRest[e]; float force = cloth.EdgeK[e] * stretch / len;
        float fa = cloth.EdgeSpring1[e] * force, fb = cloth.EdgeSpring2[e] * force;
        state.Accel[a] += ex * fa; state.Accel[a + 1] += ey * fa; state.Accel[a + 2] += ez * fa;
        state.Accel[b] += ex * fb; state.Accel[b + 1] += ey * fb; state.Accel[b + 2] += ez * fb;
      }
      if (cloth.ColliderCount > 0) {
        int touched = 0;
        for (int i = 0; i < cloth.ParticleCount; i++) { state.PrevContacts[i] = state.ContactBits[i]; touched |= state.ContactBits[i]; state.ContactBits[i] = 0; }
        if (touched != 0) ClothFriction(state, dt);
      }
    }

    private static void ClothIntegrate(NpcClothState state, float dt) {
      float dt2 = dt * dt;
      foreach (int i in state.Asset.Movable) {
        int k = i * 3; float retain = 1f - state.Asset.Damping[i];
        float x = state.Pos[k], y = state.Pos[k + 1], z = state.Pos[k + 2];
        state.Pos[k] = x + (x - state.OldPos[k]) * retain + state.Accel[k] * dt2;
        state.Pos[k + 1] = y + (y - state.OldPos[k + 1]) * retain + state.Accel[k + 1] * dt2;
        state.Pos[k + 2] = z + (z - state.OldPos[k + 2]) * retain + state.Accel[k + 2] * dt2;
        state.OldPos[k] = x; state.OldPos[k + 1] = y; state.OldPos[k + 2] = z;
      }
    }

    private static float ClothSubstepMotion(NpcClothState state) {
      float motion = 0f;
      foreach (int i in state.Asset.Movable) {
        int k = i * 3; float dx = state.Pos[k] - state.OldPos[k], dy = state.Pos[k + 1] - state.OldPos[k + 1], dz = state.Pos[k + 2] - state.OldPos[k + 2];
        motion = Math.Max(motion, dx*dx + dy*dy + dz*dz);
      }
      return motion;
    }

    private static void ClothAnchor(NpcClothState state) {
      foreach (int i in state.Asset.Anchored) {
        float w = state.Asset.SimWeight[i]; int k = i * 3;
        if (w >= 1f) { state.Pos[k] = state.Target[k]; state.Pos[k + 1] = state.Target[k + 1]; state.Pos[k + 2] = state.Target[k + 2]; }
        else { state.Pos[k] += (state.Target[k] - state.Pos[k]) * w; state.Pos[k + 1] += (state.Target[k + 1] - state.Pos[k + 1]) * w; state.Pos[k + 2] += (state.Target[k + 2] - state.Pos[k + 2]) * w; }
      }
    }

    private static void ClothRelax(NpcClothState state, int iterations) {
      NpcClothCompiled cloth = state.Asset;
      for (int iteration = 0; iteration < iterations; iteration++) for (int e = 0; e < cloth.EdgeN1.Length; e++) {
        int a = cloth.EdgeN1[e] * 3, b = cloth.EdgeN2[e] * 3;
        float dx = state.Pos[b] - state.Pos[a], dy = state.Pos[b + 1] - state.Pos[a + 1], dz = state.Pos[b + 2] - state.Pos[a + 2];
        float distSq = dx*dx + dy*dy + dz*dz, limit = cloth.EdgeMax[e];
        if (distSq <= limit * limit) { limit = cloth.EdgeMin[e]; if (limit <= 0f || distSq >= limit * limit) continue; }
        float dist = (float)Math.Sqrt(distSq); if (!(dist > 1e-9f)) dist = 1e-9f;
        float correction = 1f - limit / dist, ca = cloth.EdgeClamp1[e] * correction, cb = cloth.EdgeClamp2[e] * correction;
        state.Pos[a] += dx * ca; state.Pos[a + 1] += dy * ca; state.Pos[a + 2] += dz * ca;
        state.Pos[b] += dx * cb; state.Pos[b + 1] += dy * cb; state.Pos[b + 2] += dz * cb;
      }
    }

    private static void ClothPlaceColliders(NpcClothState state) {
      NpcClothCompiled cloth = state.Asset;
      for (int c = 0; c < cloth.ColliderCount; c++) {
        state.ColliderPrev[c] = state.ColliderWorld[c];
        state.ColliderWorld[c] = state.ColliderBind[c] * state.AnchorMatrix[cloth.ColParent[c]];
      }
      if (!state.ColliderPlaced) { Array.Copy(state.ColliderWorld, state.ColliderPrev, cloth.ColliderCount); state.ColliderPlaced = true; }
      for (int c = 0; c < cloth.ColliderCount; c++) {
        Matrix world = state.ColliderWorld[c]; int p = c * 8;
        float tx = world.M41, ty = world.M42, tz = world.M43, ux = world.M21, uy = world.M22, uz = world.M23;
        state.ColliderPrim[p + 6] = cloth.ColRadius[c];
        if (cloth.ColClass[c] == 0) {
          state.ColliderPrim[p] = ux; state.ColliderPrim[p + 1] = uy; state.ColliderPrim[p + 2] = uz;
          state.ColliderPrim[p + 7] = -(tx * ux + ty * uy + tz * uz + cloth.ColRadius[c]);
        } else if (cloth.ColClass[c] == 1) {
          state.ColliderPrim[p] = tx; state.ColliderPrim[p + 1] = ty; state.ColliderPrim[p + 2] = tz;
        } else {
          float half = cloth.ColHeight[c] * .5f;
          state.ColliderPrim[p] = tx + ux * half; state.ColliderPrim[p + 1] = ty + uy * half; state.ColliderPrim[p + 2] = tz + uz * half;
          state.ColliderPrim[p + 3] = tx - ux * half; state.ColliderPrim[p + 4] = ty - uy * half; state.ColliderPrim[p + 5] = tz - uz * half;
        }
      }
    }

    private static void ClothCollide(NpcClothState state) {
      NpcClothCompiled cloth = state.Asset;
      for (int i = 0; i < cloth.ParticleCount; i++) {
        uint bits = unchecked((uint)cloth.ParticleMask[i]); if (bits == 0) continue;
        int k = i * 3; float px = state.Pos[k], py = state.Pos[k + 1], pz = state.Pos[k + 2]; int hits = 0;
        for (int c = 0; bits != 0 && c < cloth.ColliderCount; bits >>= 1, c++) {
          if ((bits & 1) == 0) continue;
          int p = c * 8; float radius = state.ColliderPrim[p + 6], cx, cy, cz;
          if (cloth.ColClass[c] == 0) {
            float signed = px * state.ColliderPrim[p] + py * state.ColliderPrim[p + 1] + pz * state.ColliderPrim[p + 2] + state.ColliderPrim[p + 7];
            if (signed >= 0f) continue;
            px -= signed * state.ColliderPrim[p]; py -= signed * state.ColliderPrim[p + 1]; pz -= signed * state.ColliderPrim[p + 2]; hits |= 1 << c; continue;
          }
          if (cloth.ColClass[c] == 1) { cx = state.ColliderPrim[p]; cy = state.ColliderPrim[p + 1]; cz = state.ColliderPrim[p + 2]; }
          else {
            float ax = state.ColliderPrim[p], ay = state.ColliderPrim[p + 1], az = state.ColliderPrim[p + 2];
            float ex = state.ColliderPrim[p + 3] - ax, ey = state.ColliderPrim[p + 4] - ay, ez = state.ColliderPrim[p + 5] - az;
            float lengthSq = ex*ex + ey*ey + ez*ez; float t = lengthSq > 0f ? ((px-ax)*ex + (py-ay)*ey + (pz-az)*ez) / lengthSq : 0f;
            t = Math.Max(0f, Math.Min(1f, t)); cx = ax + ex*t; cy = ay + ey*t; cz = az + ez*t;
          }
          float vx = px-cx, vy = py-cy, vz = pz-cz, distSq = vx*vx + vy*vy + vz*vz;
          if (distSq > radius*radius || !(radius > 1e-8f)) continue;
          float push = (radius - (float)Math.Sqrt(distSq)) / radius;
          px += vx*push; py += vy*push; pz += vz*push; hits |= 1 << c;
        }
        if (hits != 0) { state.Pos[k] = px; state.Pos[k + 1] = py; state.Pos[k + 2] = pz; state.ContactBits[i] = hits; }
      }
    }

    private static void ClothFriction(NpcClothState state, float dt) {
      NpcClothCompiled cloth = state.Asset; float drag = -NpcClothFrictionDrag / Math.Max(1e-6f, dt);
      for (int i = 0; i < cloth.ParticleCount; i++) {
        uint bits = unchecked((uint)state.PrevContacts[i]); if (bits == 0) continue;
        int k = i * 3; float mx = state.Pos[k]-state.OldPos[k], my = state.Pos[k+1]-state.OldPos[k+1], mz = state.Pos[k+2]-state.OldPos[k+2];
        if (mx*mx + my*my + mz*mz <= 1e-8f) continue;
        for (int c = 0; bits != 0 && c < cloth.ColliderCount; bits >>= 1, c++) {
          if ((bits & 1) == 0 || cloth.ColFriction[c] == 0f) continue;
          Matrix prev = state.ColliderPrev[c];
          float dx = mx*prev.M11 + my*prev.M12 + mz*prev.M13;
          float dy = mx*prev.M21 + my*prev.M22 + mz*prev.M23;
          float dz = mx*prev.M31 + my*prev.M32 + mz*prev.M33;
          float nx, ny, nz;
          if (cloth.ColClass[c] == 0) { nx = prev.M21; ny = prev.M22; nz = prev.M23; }
          else {
            float rx = state.OldPos[k]-prev.M41, ry = state.OldPos[k+1]-prev.M42, rz = state.OldPos[k+2]-prev.M43;
            nx = rx*prev.M11 + ry*prev.M12 + rz*prev.M13;
            ny = rx*prev.M21 + ry*prev.M22 + rz*prev.M23;
            nz = rx*prev.M31 + ry*prev.M32 + rz*prev.M33;
            if (cloth.ColClass[c] != 1) { float half = cloth.ColHeight[c]*.5f; ny -= ny > half ? half : ny < -half ? -half : ny; }
            float len = (float)Math.Sqrt(nx*nx + ny*ny + nz*nz); if (!(len > 1e-9f)) continue; nx/=len; ny/=len; nz/=len;
          }
          float along = nx*dx + ny*dy + nz*dz, tx = dx-nx*along, ty = dy-ny*along, tz = dz-nz*along;
          float scale = drag * cloth.ColFriction[c];
          state.Accel[k] += (prev.M11*tx + prev.M21*ty + prev.M31*tz)*scale;
          state.Accel[k+1] += (prev.M12*tx + prev.M22*ty + prev.M32*tz)*scale;
          state.Accel[k+2] += (prev.M13*tx + prev.M23*ty + prev.M33*tz)*scale;
        }
      }
    }

    private static void ClothWriteBones(NpcClothState state) {
      NpcClothCompiled cloth = state.Asset;
      for (int p = 0; p < cloth.ParentNames.Length; p++) {
        if (!NpcTryJedipediaBindRotation(state.AnchorMatrix[p], out Quaternion q)) q = Quaternion.Identity;
        state.AnchorQuat[p] = ClothQuatNormalize(q);
      }
      for (int i = 0; i < cloth.ParticleCount; i++) {
        Quaternion parentRotation = state.AnchorQuat[cloth.ParentIndex[i]], rotation = parentRotation;
        int tip = !cloth.Rigid && !cloth.TipSkinned[i] ? cloth.Tip[i] : -1;
        if (tip >= 0) {
          int k = i*3, t = tip*3;
          Vector3 restAxis = new Vector3(cloth.RestPos[t]-cloth.RestPos[k], cloth.RestPos[t+1]-cloth.RestPos[k+1], cloth.RestPos[t+2]-cloth.RestPos[k+2]);
          Vector3 currentAxis = new Vector3(state.Pos[t]-state.Pos[k], state.Pos[t+1]-state.Pos[k+1], state.Pos[t+2]-state.Pos[k+2]);
          if (restAxis.LengthSquared() > 1e-12f && currentAxis.LengthSquared() > 1e-12f) {
            restAxis.Normalize(); currentAxis.Normalize(); restAxis = ClothTransformNormal(restAxis, NpcJedipediaRotationMatrix(parentRotation)); restAxis.Normalize();
            if (Vector3.Dot(restAxis, currentAxis) <= NpcClothDeadzoneCos) rotation = ClothQuatMultiply(ClothRotationTo(restAxis, currentAxis), parentRotation);
          }
        }
        int baseOffset = i*3; Vector3 rest = new Vector3(cloth.RestPos[baseOffset], cloth.RestPos[baseOffset+1], cloth.RestPos[baseOffset+2]);
        Vector3 pos = new Vector3(state.Pos[baseOffset], state.Pos[baseOffset+1], state.Pos[baseOffset+2]);
        Matrix matrix = NpcJedipediaRotationMatrix(rotation); Vector3 rotatedRest = ClothTransformNormal(rest, matrix);
        matrix.M41 = pos.X - rotatedRest.X; matrix.M42 = pos.Y - rotatedRest.Y; matrix.M43 = pos.Z - rotatedRest.Z; matrix.M44 = 1f;
        state.BoneMatrices[i] = matrix;
      }
    }

    private void ClearNpcClothRuntime() {
      npcClothStates.Clear();
      npcClothCompiled.Clear();
    }
  }
}
