using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace NohddX.Ui.Helpers;

/// <summary>
/// Shared helpers for building 3D server-rack visualizations with WPF Viewport3D.
/// </summary>
public static class Viewport3DHelper
{
    /// <summary>
    /// Creates a box MeshGeometry3D centred at (<paramref name="cx"/>, <paramref name="cy"/>,
    /// <paramref name="cz"/>) with the given dimensions.  Six faces, outward-facing normals.
    /// </summary>
    public static MeshGeometry3D CreateBox(
        double cx, double cy, double cz,
        double width, double height, double depth)
    {
        double hw = width / 2, hh = height / 2, hd = depth / 2;

        var mesh = new MeshGeometry3D();

        // 8 corners
        Point3D p0 = new(cx - hw, cy - hh, cz + hd); // front-bottom-left
        Point3D p1 = new(cx + hw, cy - hh, cz + hd); // front-bottom-right
        Point3D p2 = new(cx + hw, cy + hh, cz + hd); // front-top-right
        Point3D p3 = new(cx - hw, cy + hh, cz + hd); // front-top-left
        Point3D p4 = new(cx - hw, cy - hh, cz - hd); // back-bottom-left
        Point3D p5 = new(cx + hw, cy - hh, cz - hd); // back-bottom-right
        Point3D p6 = new(cx + hw, cy + hh, cz - hd); // back-top-right
        Point3D p7 = new(cx - hw, cy + hh, cz - hd); // back-top-left

        // Helper – adds a quad (2 triangles) with its normal.
        void AddFace(Point3D a, Point3D b, Point3D c, Point3D d, Vector3D normal)
        {
            int idx = mesh.Positions.Count;
            mesh.Positions.Add(a);
            mesh.Positions.Add(b);
            mesh.Positions.Add(c);
            mesh.Positions.Add(d);
            mesh.Normals.Add(normal);
            mesh.Normals.Add(normal);
            mesh.Normals.Add(normal);
            mesh.Normals.Add(normal);
            mesh.TriangleIndices.Add(idx);
            mesh.TriangleIndices.Add(idx + 1);
            mesh.TriangleIndices.Add(idx + 2);
            mesh.TriangleIndices.Add(idx);
            mesh.TriangleIndices.Add(idx + 2);
            mesh.TriangleIndices.Add(idx + 3);
        }

        AddFace(p0, p1, p2, p3, new Vector3D(0, 0, 1));   // front
        AddFace(p5, p4, p7, p6, new Vector3D(0, 0, -1));   // back
        AddFace(p4, p0, p3, p7, new Vector3D(-1, 0, 0));   // left
        AddFace(p1, p5, p6, p2, new Vector3D(1, 0, 0));    // right
        AddFace(p3, p2, p6, p7, new Vector3D(0, 1, 0));    // top
        AddFace(p4, p5, p1, p0, new Vector3D(0, -1, 0));   // bottom

        return mesh;
    }

    /// <summary>
    /// Builds a <see cref="MaterialGroup"/> that simulates brushed metal:
    /// a diffuse tint plus a white specular highlight.
    /// </summary>
    public static MaterialGroup CreateMetalMaterial(Color baseColor, double specularPower = 85)
    {
        var group = new MaterialGroup();

        // Diffuse with a subtle vertical gradient for "brushed" look
        var diffuseGradient = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        diffuseGradient.GradientStops.Add(new GradientStop(
            Color.FromArgb(baseColor.A,
                           (byte)Math.Min(255, baseColor.R + 25),
                           (byte)Math.Min(255, baseColor.G + 25),
                           (byte)Math.Min(255, baseColor.B + 25)), 0));
        diffuseGradient.GradientStops.Add(new GradientStop(baseColor, 0.5));
        diffuseGradient.GradientStops.Add(new GradientStop(
            Color.FromArgb(baseColor.A,
                           (byte)Math.Max(0, baseColor.R - 20),
                           (byte)Math.Max(0, baseColor.G - 20),
                           (byte)Math.Max(0, baseColor.B - 20)), 1));

        group.Children.Add(new DiffuseMaterial(diffuseGradient));
        group.Children.Add(new SpecularMaterial(Brushes.White, specularPower));

        return group;
    }

    /// <summary>
    /// Convenience: creates a server-unit box (GeometryModel3D) at the given position
    /// with default server-unit dimensions (2 x 0.3 x 1).
    /// </summary>
    public static GeometryModel3D CreateServerNode(
        double x, double y, double z, Color color,
        double width = 2, double height = 0.3, double depth = 1)
    {
        var mesh = CreateBox(x, y, z, width, height, depth);
        var material = CreateMetalMaterial(color);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    /// <summary>
    /// Builds a semi-transparent rack frame with <paramref name="slotCount"/> unit slots.
    /// The rack is a set of thin bars that outline the rack volume.
    /// </summary>
    public static Model3DGroup CreateRackFrame(int slotCount)
    {
        var group = new Model3DGroup();

        double rackWidth = 2.2;
        double rackDepth = 1.1;
        double slotHeight = 0.35;
        double rackHeight = slotCount * slotHeight + 0.1;
        double barThickness = 0.04;

        var frameMaterial = CreateMetalMaterial(Color.FromRgb(80, 85, 95), 120);

        // Vertical posts (4 corners)
        double hw = rackWidth / 2;
        double hd = rackDepth / 2;
        double centerY = rackHeight / 2;

        group.Children.Add(new GeometryModel3D(
            CreateBox(-hw, centerY, hd, barThickness, rackHeight, barThickness), frameMaterial));
        group.Children.Add(new GeometryModel3D(
            CreateBox(hw, centerY, hd, barThickness, rackHeight, barThickness), frameMaterial));
        group.Children.Add(new GeometryModel3D(
            CreateBox(-hw, centerY, -hd, barThickness, rackHeight, barThickness), frameMaterial));
        group.Children.Add(new GeometryModel3D(
            CreateBox(hw, centerY, -hd, barThickness, rackHeight, barThickness), frameMaterial));

        // Horizontal rails – top and bottom, front and back
        foreach (double y in new[] { 0.0, rackHeight })
        {
            group.Children.Add(new GeometryModel3D(
                CreateBox(0, y, hd, rackWidth, barThickness, barThickness), frameMaterial));
            group.Children.Add(new GeometryModel3D(
                CreateBox(0, y, -hd, rackWidth, barThickness, barThickness), frameMaterial));
        }

        // Depth rails – top and bottom
        foreach (double y in new[] { 0.0, rackHeight })
        {
            group.Children.Add(new GeometryModel3D(
                CreateBox(-hw, y, 0, barThickness, barThickness, rackDepth), frameMaterial));
            group.Children.Add(new GeometryModel3D(
                CreateBox(hw, y, 0, barThickness, barThickness, rackDepth), frameMaterial));
        }

        return group;
    }
}
