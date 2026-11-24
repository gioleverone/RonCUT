using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Material3D = System.Windows.Media.Media3D.Material;

namespace RonCUT;

public sealed class CutPreview : Viewport3D
{
    private readonly OrthographicCamera _camera = new OrthographicCamera();
    private readonly Model3DGroup _scene = new Model3DGroup();
    private readonly TranslateTransform3D _toolPosition = new TranslateTransform3D();
    private readonly AxisAngleRotation3D _toolRotation = new AxisAngleRotation3D(new Vector3D(0, 0, 1), 0);
    private readonly ModelVisual3D _toolVisual = new ModelVisual3D();
    private readonly SolidColorBrush _toolIndicator = new SolidColorBrush(Colors.SlateGray);
    private Point _lastMouse;
    private bool _dragging;
    private Point3D _center;
    private double _distance = 150;
    private double _toolHeight;
    private readonly double _extent;
    private double _toolDiameter;
    private double _yaw = -90;
    private double _pitch = 90;
    public double NominalSeconds { get; private set; }

    public CutPreview(string gcode, double toolDiameter)
        : this(new[] { new CutPhase(gcode, "", toolDiameter) })
    {
    }

    public CutPreview(IReadOnlyList<CutPhase> phases)
    {
        ClipToBounds = true;
        Camera = _camera;
        _scene.Children.Add(new AmbientLight(Color.FromRgb(190, 190, 190)));
        _scene.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, 2, -3)));
        var cuts = new List<(Point3D A, Point3D B)>();
        var rapid = new List<(Point3D A, Point3D B)>();
        for (int i = 0; i < phases.Count; i++)
        {
            Parse(phases[i].Gcode, cuts, rapid, startsAfterChange: i > 0);
        }
        var all = cuts.Concat(rapid).SelectMany(s => new[] { s.A, s.B }).ToList();
        if (all.Count == 0)
        {
            all.Add(new Point3D());
            all.Add(new Point3D(10, 10, 0));
        }
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X);
        double minY = all.Min(p => p.Y), maxY = all.Max(p => p.Y);
        double minZ = all.Min(p => p.Z), maxZ = all.Max(p => p.Z);
        _center = new Point3D((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        double extent = Math.Max(10, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
        _extent = extent;
        _distance = extent * 2.5;
        double width = Math.Max(0.08, extent / 1200);
        AddSegments(cuts, width, Color.FromRgb(30, 113, 194));
        AddSegments(rapid, width * 0.55, Color.FromRgb(164, 174, 183));
        var grid = new List<(Point3D A, Point3D B)>();
        double left = Math.Floor(minX / 10) * 10 - 10, right = Math.Ceiling(maxX / 10) * 10 + 10;
        double front = Math.Floor(minY / 10) * 10 - 10, back = Math.Ceiling(maxY / 10) * 10 + 10;
        double spacing = Math.Max(10, Math.Ceiling(Math.Max(right - left, back - front) / 500 / 10) * 10);
        for (double x = left; x <= right; x += spacing)
        {
            grid.Add((new Point3D(x, front, -0.04), new Point3D(x, back, -0.04)));
        }
        for (double y = front; y <= back; y += spacing)
        {
            grid.Add((new Point3D(left, y, -0.04), new Point3D(right, y, -0.04)));
        }
        AddSegments(grid, width * 0.3, Color.FromRgb(210, 219, 227));
        Children.Add(new ModelVisual3D { Content = _scene });
        SetToolDiameter(phases[0].ToolDiameter);
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
        SizeChanged += (_, _) =>
        {
            UpdateCamera();
        };
        SetView("top");
    }

    public void SetToolDiameter(double diameter)
    {
        if (_toolDiameter != diameter || _toolVisual.Content == null)
        {
            _toolDiameter = diameter;
            BuildTool(Math.Max(diameter, 0.1), _extent);
            UpdateCamera();
        }
    }

    private void Parse(string code, List<(Point3D A, Point3D B)> cuts, List<(Point3D A, Point3D B)> rapids, bool startsAfterChange)
    {
        var point = new Point3D();
        bool knownX = !startsAfterChange, knownY = !startsAfterChange;
        double feed = 0;
        foreach (string raw in code.Split('\n'))
        {
            string line = raw.Trim();
            bool isRapid = Regex.IsMatch(line, @"^G0(?:\s|$)");
            bool isCut = Regex.IsMatch(line, @"^G1(?:\s|$)");
            if (!isRapid && !isCut)
            {
                continue;
            }
            var end = point;
            bool knownStart = knownX && knownY;
            foreach (Match m in Regex.Matches(line, @"([XYZF])\s*([-+]?\d+(?:\.\d+)?)"))
            {
                if (!double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                {
                    continue;
                }
                switch (m.Groups[1].Value)
                {
                    case "X": end.X = v; knownX = true; break;
                    case "Y": end.Y = v; knownY = true; break;
                    case "Z": end.Z = v; break;
                    case "F": feed = v; break;
                }
            }
            if (knownStart && (end - point).Length > 0.000001)
            {
                (isRapid ? rapids : cuts).Add((point, end));
                if (isCut && feed > 0)
                {
                    NominalSeconds += (end - point).Length / feed * 60;
                }
            }
            point = end;
        }
    }

    private void AddSegments(IEnumerable<(Point3D A, Point3D B)> segments, double width, Color color)
    {
        var mesh = new MeshGeometry3D();
        foreach (var segment in segments)
        {
            var direction = segment.B - segment.A;
            var side = Vector3D.CrossProduct(direction, new Vector3D(0, 0, 1));
            if (side.Length < 0.00001)
            {
                side = Vector3D.CrossProduct(direction, new Vector3D(0, 1, 0));
            }
            side.Normalize();
            side *= width;
            int index = mesh.Positions.Count;
            mesh.Positions.Add(segment.A - side);
            mesh.Positions.Add(segment.A + side);
            mesh.Positions.Add(segment.B + side);
            mesh.Positions.Add(segment.B - side);
            foreach (int i in new[] { 0, 1, 2, 0, 2, 3 })
            {
                mesh.TriangleIndices.Add(index + i);
            }
        }
        var material = new DiffuseMaterial(new SolidColorBrush(color));
        _scene.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private void BuildTool(double diameter, double extent)
    {
        double radius = diameter / 2;
        double bodyRadius = Math.Max(diameter * 1.4, extent / 60);
        double bitHeight = Math.Max(diameter * 2.5, bodyRadius * 1.5);
        double colletTop = bitHeight + bodyRadius * 0.65;
        double bodyBottom = colletTop + bodyRadius * 0.9;
        double bodyTop = bodyBottom + bodyRadius * 3.4;
        var silver = Metal(Color.FromRgb(194, 207, 219));
        var dark = Metal(Color.FromRgb(48, 62, 78));
        var blue = Metal(Color.FromRgb(35, 112, 174));
        var gold = Metal(Color.FromRgb(221, 179, 72));
        var tool = new Model3DGroup();
        var cutting = new Model3DGroup
        {
            Transform = new RotateTransform3D(_toolRotation)
        };
        AddBody(cutting, 0, bitHeight, radius, radius, silver);
        for (int flute = 0; flute < 2; flute++)
        {
            var mesh = new MeshGeometry3D();
            for (int i = 0; i < 32; i++)
            {
                double z1 = bitHeight * i / 32;
                double z2 = bitHeight * (i + 1) / 32;
                double a1 = flute * Math.PI + i * Math.PI / 20;
                double a2 = flute * Math.PI + (i + 1) * Math.PI / 20;
                int n = mesh.Positions.Count;
                mesh.Positions.Add(new Point3D(radius * 1.015 * Math.Cos(a1), radius * 1.015 * Math.Sin(a1), z1));
                mesh.Positions.Add(new Point3D(radius * 1.015 * Math.Cos(a1 + 0.35), radius * 1.015 * Math.Sin(a1 + 0.35), z1));
                mesh.Positions.Add(new Point3D(radius * 1.015 * Math.Cos(a2 + 0.35), radius * 1.015 * Math.Sin(a2 + 0.35), z2));
                mesh.Positions.Add(new Point3D(radius * 1.015 * Math.Cos(a2), radius * 1.015 * Math.Sin(a2), z2));
                AddQuad(mesh, n);
            }
            cutting.Children.Add(new GeometryModel3D(mesh, gold) { BackMaterial = gold });
        }
        AddBody(cutting, bitHeight, colletTop, bodyRadius * 0.45, bodyRadius * 0.6, dark, 12);
        tool.Children.Add(cutting);
        AddBody(tool, colletTop, bodyBottom, bodyRadius * 0.6, bodyRadius, silver);
        AddBody(tool, bodyBottom, bodyTop, bodyRadius, bodyRadius, silver);
        AddBody(tool, bodyBottom, bodyBottom + bodyRadius * 0.35, bodyRadius * 1.03, bodyRadius * 1.03, blue);
        for (int band = 0; band < 3; band++)
        {
            double z = bodyTop - bodyRadius * (0.9 + band * 0.3);
            AddBody(tool, z, z + bodyRadius * 0.1, bodyRadius * 1.015, bodyRadius * 1.015, dark);
        }
        AddBody(tool, bodyTop, bodyTop + bodyRadius * 0.3, bodyRadius * 1.02, bodyRadius * 0.95, blue);
        double lid = bodyTop + bodyRadius * 0.3;
        _toolHeight = lid + bodyRadius * 0.12;
        AddBody(tool, lid, lid + bodyRadius * 0.07, bodyRadius * 0.78, bodyRadius * 0.78, dark);
        var fan = new Model3DGroup
        {
            Transform = new RotateTransform3D(_toolRotation)
        };
        for (int blade = 0; blade < 6; blade++)
        {
            double a = blade * Math.PI / 3;
            var mesh = new MeshGeometry3D();
            double z = lid + bodyRadius * 0.08;
            mesh.Positions.Add(new Point3D(bodyRadius * 0.22 * Math.Cos(a), bodyRadius * 0.22 * Math.Sin(a), z));
            mesh.Positions.Add(new Point3D(bodyRadius * 0.65 * Math.Cos(a + 0.25), bodyRadius * 0.65 * Math.Sin(a + 0.25), z));
            mesh.Positions.Add(new Point3D(bodyRadius * 0.65 * Math.Cos(a + 0.65), bodyRadius * 0.65 * Math.Sin(a + 0.65), z));
            mesh.Positions.Add(new Point3D(bodyRadius * 0.22 * Math.Cos(a + 0.4), bodyRadius * 0.22 * Math.Sin(a + 0.4), z));
            AddQuad(mesh, 0);
            fan.Children.Add(new GeometryModel3D(mesh, silver) { BackMaterial = silver });
        }
        tool.Children.Add(fan);
        AddBody(tool, lid + bodyRadius * 0.09, lid + bodyRadius * 0.12, bodyRadius * 0.18, bodyRadius * 0.18, new EmissiveMaterial(_toolIndicator));
        tool.Transform = _toolPosition;
        _toolVisual.Content = tool;
    }

    private static Material3D Metal(Color color)
    {
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(225, 235, 245)), 65));
        return material;
    }

    private static void AddQuad(MeshGeometry3D mesh, int start)
    {
        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.TriangleIndices.Add(start + index);
        }
    }

    private static void AddBody(Model3DGroup model, double bottom, double top, double lowerRadius, double upperRadius, Material3D material, int sides = 32)
    {
        var mesh = new MeshGeometry3D();
        for (int i = 0; i < sides; i++)
        {
            double a = 2 * Math.PI * i / sides;
            double b = 2 * Math.PI * (i + 1) / sides;
            int n = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(lowerRadius * Math.Cos(a), lowerRadius * Math.Sin(a), bottom));
            mesh.Positions.Add(new Point3D(lowerRadius * Math.Cos(b), lowerRadius * Math.Sin(b), bottom));
            mesh.Positions.Add(new Point3D(upperRadius * Math.Cos(b), upperRadius * Math.Sin(b), top));
            mesh.Positions.Add(new Point3D(upperRadius * Math.Cos(a), upperRadius * Math.Sin(a), top));
            for (int corner = 0; corner < 4; corner++)
            {
                double angle = corner is 0 or 3 ? a : b;
                var normal = new Vector3D(Math.Cos(angle), Math.Sin(angle), (lowerRadius - upperRadius) / (top - bottom));
                normal.Normalize();
                mesh.Normals.Add(normal);
            }
            AddQuad(mesh, n);
            int cap = mesh.Positions.Count;
            mesh.Positions.Add(new Point3D(0, 0, top));
            mesh.Positions.Add(new Point3D(upperRadius * Math.Cos(a), upperRadius * Math.Sin(a), top));
            mesh.Positions.Add(new Point3D(upperRadius * Math.Cos(b), upperRadius * Math.Sin(b), top));
            mesh.Normals.Add(new Vector3D(0, 0, 1));
            mesh.Normals.Add(new Vector3D(0, 0, 1));
            mesh.Normals.Add(new Vector3D(0, 0, 1));
            mesh.TriangleIndices.Add(cap);
            mesh.TriangleIndices.Add(cap + 1);
            mesh.TriangleIndices.Add(cap + 2);
        }
        model.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    public void ShowPosition(MachinePosition? position, bool rotating)
    {
        if (position == null)
        {
            Children.Remove(_toolVisual);
            return;
        }
        if (!Children.Contains(_toolVisual))
        {
            Children.Add(_toolVisual);
        }
        _toolPosition.OffsetX = position.X;
        _toolPosition.OffsetY = position.Y;
        _toolPosition.OffsetZ = position.Z;
        _toolIndicator.Color = rotating ? Color.FromRgb(51, 223, 163) : Color.FromRgb(114, 135, 154);
        if (rotating)
        {
            _toolRotation.Angle = (_toolRotation.Angle + 24) % 360;
        }
    }

    public void SetView(string name)
    {
        if (name == "top")
        {
            _yaw = -90;
            _pitch = 90;
        }
        else
        {
            _yaw = -45;
            _pitch = 45;
        }
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        double yaw = _yaw * Math.PI / 180;
        double pitch = _pitch * Math.PI / 180;
        bool top = _pitch >= 89.999;
        var v = top ? new Vector3D(0, 0, 1) : new Vector3D(Math.Cos(pitch) * Math.Cos(yaw), Math.Cos(pitch) * Math.Sin(yaw), Math.Sin(pitch));
        double cameraDistance = Math.Max(_distance, _toolHeight * 2);
        _camera.Position = _center + v * cameraDistance;
        _camera.LookDirection = _center - _camera.Position;
        _camera.UpDirection = top ? new Vector3D(0, 1, 0) : new Vector3D(0, 0, 1);
        double aspect = ActualHeight > 0 ? ActualWidth / ActualHeight : 1;
        _camera.Width = _distance * 0.5 * Math.Max(1, aspect);
        _camera.NearPlaneDistance = Math.Max(0.01, cameraDistance / 10000);
        _camera.FarPlaneDistance = cameraDistance * 20;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            _dragging = true;
            _lastMouse = e.GetPosition(this);
            CaptureMouse();
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            Point p = e.GetPosition(this);
            _yaw -= (p.X - _lastMouse.X) * 0.5;
            _pitch = Math.Clamp(_pitch + (p.Y - _lastMouse.Y) * 0.5, -89, 89);
            _lastMouse = p;
            UpdateCamera();
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1.18), 1, 100000);
        UpdateCamera();
    }
}
