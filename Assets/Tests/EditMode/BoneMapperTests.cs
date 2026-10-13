using System.Collections.Generic;
using NUnit.Framework;
using Shimeji.Core;

namespace Shimeji.Tests
{
    public class BoneMapperTests
    {
        static List<BoneNode> Nodes(params (string name, int depth)[] items)
        {
            var l = new List<BoneNode>();
            foreach (var (n, d) in items) l.Add(new BoneNode(n, d));
            return l;
        }

        static void AssertHumanoid(BoneMapResult r)
        {
            Assert.IsTrue(r.IsValid, "missing: " + string.Join(",", r.MissingRequired));
            Assert.IsTrue(r.CanBuildHumanoid, "cannot build humanoid. mapped: " + string.Join(",", r.Map.Keys));
        }

        [Test]
        public void Mixamo()
        {
            var r = BoneMapper.Map(Nodes(
                ("mixamorig:Hips", 1), ("mixamorig:Spine", 2), ("mixamorig:Spine1", 3), ("mixamorig:Spine2", 4),
                ("mixamorig:Neck", 5), ("mixamorig:Head", 6), ("mixamorig:HeadTop_End", 7),
                ("mixamorig:LeftShoulder", 5), ("mixamorig:LeftArm", 6), ("mixamorig:LeftForeArm", 7), ("mixamorig:LeftHand", 8),
                ("mixamorig:LeftHandIndex1", 9), ("mixamorig:LeftHandThumb1", 9),
                ("mixamorig:RightShoulder", 5), ("mixamorig:RightArm", 6), ("mixamorig:RightForeArm", 7), ("mixamorig:RightHand", 8),
                ("mixamorig:LeftUpLeg", 2), ("mixamorig:LeftLeg", 3), ("mixamorig:LeftFoot", 4), ("mixamorig:LeftToeBase", 5), ("mixamorig:LeftToe_End", 6),
                ("mixamorig:RightUpLeg", 2), ("mixamorig:RightLeg", 3), ("mixamorig:RightFoot", 4), ("mixamorig:RightToeBase", 5)));
            AssertHumanoid(r);
            Assert.AreEqual("mixamorig:Hips", r.Map[BoneKey.Hips]);
            Assert.AreEqual("mixamorig:Spine", r.Map[BoneKey.Spine]);
            Assert.AreEqual("mixamorig:Spine1", r.Map[BoneKey.Chest]);
            Assert.AreEqual("mixamorig:Spine2", r.Map[BoneKey.UpperChest]);
            Assert.AreEqual("mixamorig:Head", r.Map[BoneKey.Head]);
            Assert.AreEqual("mixamorig:LeftArm", r.Map[BoneKey.LeftUpperArm]);
            Assert.AreEqual("mixamorig:LeftForeArm", r.Map[BoneKey.LeftLowerArm]);
            Assert.AreEqual("mixamorig:LeftHand", r.Map[BoneKey.LeftHand]);
            Assert.AreEqual("mixamorig:LeftUpLeg", r.Map[BoneKey.LeftUpperLeg]);
            Assert.AreEqual("mixamorig:LeftLeg", r.Map[BoneKey.LeftLowerLeg]);
            Assert.AreEqual("mixamorig:RightToeBase", r.Map[BoneKey.RightToes]);
        }

        [Test]
        public void Bip001()
        {
            var r = BoneMapper.Map(Nodes(
                ("Bip001", 0), ("Bip001 Pelvis", 1), ("Bip001 Spine", 2), ("Bip001 Spine1", 3), ("Bip001 Neck", 4), ("Bip001 Head", 5),
                ("Bip001 L Clavicle", 4), ("Bip001 L UpperArm", 5), ("Bip001 L Forearm", 6), ("Bip001 L Hand", 7), ("Bip001 L Finger0", 8),
                ("Bip001 R Clavicle", 4), ("Bip001 R UpperArm", 5), ("Bip001 R Forearm", 6), ("Bip001 R Hand", 7),
                ("Bip001 L Thigh", 2), ("Bip001 L Calf", 3), ("Bip001 L Foot", 4), ("Bip001 L Toe0", 5),
                ("Bip001 R Thigh", 2), ("Bip001 R Calf", 3), ("Bip001 R Foot", 4), ("Bip001 R Toe0", 5),
                ("Bip001 Footsteps", 1), ("Bip001 HeadNub", 6)));
            AssertHumanoid(r);
            Assert.AreEqual("Bip001 Pelvis", r.Map[BoneKey.Hips]);
            Assert.AreEqual("Bip001 Spine1", r.Map[BoneKey.Chest]);
            Assert.AreEqual("Bip001 L Clavicle", r.Map[BoneKey.LeftShoulder]);
            Assert.AreEqual("Bip001 R Calf", r.Map[BoneKey.RightLowerLeg]);
            Assert.AreEqual("Bip001 L Foot", r.Map[BoneKey.LeftFoot]);
            Assert.AreEqual("Bip001 Head", r.Map[BoneKey.Head]);
        }

        [Test]
        public void Vrm()
        {
            var names = new[]
            {
                "J_Bip_C_Hips", "J_Bip_C_Spine", "J_Bip_C_Chest", "J_Bip_C_UpperChest", "J_Bip_C_Neck", "J_Bip_C_Head",
                "J_Adj_L_FaceEye", "J_Sec_Hair1_01", "J_Sec_L_Bust1",
                "J_Bip_L_Shoulder", "J_Bip_L_UpperArm", "J_Bip_L_LowerArm", "J_Bip_L_Hand", "J_Bip_L_Index1",
                "J_Bip_R_Shoulder", "J_Bip_R_UpperArm", "J_Bip_R_LowerArm", "J_Bip_R_Hand",
                "J_Bip_L_UpperLeg", "J_Bip_L_LowerLeg", "J_Bip_L_Foot", "J_Bip_L_ToeBase",
                "J_Bip_R_UpperLeg", "J_Bip_R_LowerLeg", "J_Bip_R_Foot", "J_Bip_R_ToeBase",
            };
            var nodes = new List<BoneNode>();
            for (int i = 0; i < names.Length; i++) nodes.Add(new BoneNode(names[i], i));
            var r = BoneMapper.Map(nodes);
            AssertHumanoid(r);
            Assert.AreEqual("J_Bip_C_Chest", r.Map[BoneKey.Chest]);
            Assert.AreEqual("J_Bip_C_UpperChest", r.Map[BoneKey.UpperChest]);
            Assert.AreEqual("J_Bip_L_UpperArm", r.Map[BoneKey.LeftUpperArm]);
            Assert.AreEqual("J_Bip_R_LowerLeg", r.Map[BoneKey.RightLowerLeg]);
            Assert.IsFalse(r.Map.ContainsValue("J_Sec_Hair1_01"));
            Assert.IsFalse(r.Map.ContainsValue("J_Bip_L_Index1"));
        }

        [Test]
        public void BlenderDotSuffix()
        {
            var r = BoneMapper.Map(Nodes(
                ("Armature", 0), ("hips", 1), ("spine", 2), ("chest", 3), ("neck", 4), ("head", 5),
                ("upper_arm.L", 4), ("forearm.L", 5), ("hand.L", 6), ("upper_arm.R", 4), ("forearm.R", 5), ("hand.R", 6),
                ("thigh.L", 2), ("shin.L", 3), ("foot.L", 4), ("toe.L", 5),
                ("thigh.R", 2), ("shin.R", 3), ("foot.R", 4), ("toe.R", 5)));
            AssertHumanoid(r);
            Assert.AreEqual("thigh.L", r.Map[BoneKey.LeftUpperLeg]);
            Assert.AreEqual("shin.R", r.Map[BoneKey.RightLowerLeg]);
            Assert.AreEqual("upper_arm.R", r.Map[BoneKey.RightUpperArm]);
        }

        [Test]
        public void GenericCreature_ValidButNotHumanoid()
        {
            var r = BoneMapper.Map(Nodes(("Root", 0), ("Pelvis", 1), ("Body", 2), ("Head", 3), ("Arm_L", 3), ("Hand_L", 4),
                ("Leg_L", 2), ("Foot_L", 3), ("Leg_R", 2), ("Foot_R", 3), ("Tail1", 2)));
            Assert.IsTrue(r.IsValid);
            Assert.IsFalse(r.CanBuildHumanoid);
            Assert.AreEqual("Leg_L", r.Map[BoneKey.LeftUpperLeg]); // "Leg" 단독 + UpLeg 없음 → UpperLeg
            Assert.AreEqual("Pelvis", r.Map[BoneKey.Hips]);
        }

        [Test]
        public void ArbitraryNamesFailRequiredBones()
        {
            var r = BoneMapper.Map(Nodes(("bone_000", 0), ("bone_001", 1), ("bone_002", 2), ("Cube", 1)));
            Assert.IsFalse(r.IsValid);
            CollectionAssert.AreEquivalent(BoneMapper.Required, r.MissingRequired);
        }

        [Test]
        public void OverrideFixesMissingBones()
        {
            var nodes = Nodes(("bone_000", 0), ("bone_001", 1), ("bone_002", 2), ("bone_003", 2));
            var overrides = new Dictionary<string, string>
            {
                ["Hips"] = "bone_000", ["head"] = "bone_001", ["LeftFoot"] = "bone_002", ["RightFoot"] = "bone_003", ["Tail"] = "x",
                ["Neck"] = "does_not_exist",
            };
            var r = BoneMapper.Map(nodes, overrides);
            Assert.IsTrue(r.IsValid);
            Assert.AreEqual("bone_001", r.Map[BoneKey.Head]);
            Assert.AreEqual(2, r.Warnings.Count); // 알 수 없는 키 + 없는 본
        }

        [Test]
        public void Tokenize()
        {
            CollectionAssert.AreEqual(new[] { "mixamorig", "left", "up", "leg" }, BoneMapper.Tokenize("mixamorig:LeftUpLeg"));
            CollectionAssert.AreEqual(new[] { "bip", "001", "l", "thigh" }, BoneMapper.Tokenize("Bip001 L Thigh"));
            CollectionAssert.AreEqual(new[] { "j", "bip", "l", "upper", "leg" }, BoneMapper.Tokenize("J_Bip_L_UpperLeg"));
            CollectionAssert.AreEqual(new[] { "thigh", "l" }, BoneMapper.Tokenize("Thigh.L"));
        }
    }
}
