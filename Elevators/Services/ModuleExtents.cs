using UnityEngine;

namespace Elevators.Services
{
    // Lowest and highest point of a room relative to its own origin, from its meshes and
    // box colliders. Works on a prefab asset as well as a spawned room, so the gap between
    // floors can be sized before anything is placed.
    internal static class ModuleExtents
    {
        public static bool TryMeasure(GameObject? root, out float bottom, out float top)
        {
            bottom = float.MaxValue;
            top = float.MinValue;
            if (root == null)
                return false;

            float origin = root.transform.position.y;
            foreach (MeshFilter filter in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.sharedMesh)
                    continue;
                Bounds b = filter.sharedMesh.bounds;
                Stretch(filter.transform.localToWorldMatrix, b.center, b.extents, ref bottom, ref top);
            }
            foreach (BoxCollider box in root.GetComponentsInChildren<BoxCollider>(true))
                Stretch(box.transform.localToWorldMatrix, box.center, box.size * 0.5f, ref bottom, ref top);

            if (bottom == float.MaxValue)
                return false;
            bottom -= origin;
            top -= origin;
            return true;
        }

        private static void Stretch(Matrix4x4 m, Vector3 center, Vector3 extents, ref float bottom, ref float top)
        {
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    center.x + ((i & 1) == 0 ? -extents.x : extents.x),
                    center.y + ((i & 2) == 0 ? -extents.y : extents.y),
                    center.z + ((i & 4) == 0 ? -extents.z : extents.z));
                float y = m.MultiplyPoint3x4(corner).y;
                if (y < bottom) bottom = y;
                if (y > top) top = y;
            }
        }
    }
}
