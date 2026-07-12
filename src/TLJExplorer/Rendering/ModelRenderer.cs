using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using TLJExplorer.Core.Formats;

// Two numerics libraries are used side-by-side: Silk.NET.Maths (Vector3D<float>, Matrix4X4<float>) for
// camera/projection, and System.Numerics (Vector3, Matrix4x4) for CPU-side skinning. Every skinning-related
// declaration below is explicitly qualified so the space is always obvious.

namespace TLJExplorer.Rendering;

/// <summary>Orbit camera parameters. Yaw/Pitch are radians; Distance is world units from Target.</summary>
public readonly record struct OrbitCamera(Vector3D<float> Target, float Yaw, float Pitch, float Distance)
{
    public Vector3D<float> EyeDirection => new(
        MathF.Cos(Pitch) * MathF.Sin(Yaw),
        MathF.Sin(Pitch),
        MathF.Cos(Pitch) * MathF.Cos(Yaw));

    public Vector3D<float> Eye => Target + EyeDirection * Distance;
}

/// <summary>
/// Renders a single <see cref="CirModel"/> using Silk.NET.OpenGL and returns the frame as a BGRA32 pixel
/// buffer suitable for a WPF <see cref="System.Windows.Media.Imaging.WriteableBitmap"/>. Uses an offscreen
/// FBO rather than an embedded native child window because Silk.NET's GLFW-backed windows cannot be
/// reliably reparented as WS_CHILD -- the readback cost is acceptable since the viewer only re-renders on
/// demand (camera changes, resize, pose changes during animation).
/// </summary>
public sealed class ModelRenderer : IDisposable
{
    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec3 aPosition;
        layout(location = 1) in vec3 aNormal;
        layout(location = 2) in vec2 aTexCoord;

        uniform mat4 uView;
        uniform mat4 uProjection;

        out vec3 vNormal;
        out vec2 vTexCoord;
        out vec3 vWorldPos;

        void main()
        {
            // Skinning is done on the CPU (see ApplyPose/BuildVertexData); aPosition is already world-space.
            vWorldPos = aPosition;
            vNormal = aNormal;
            vTexCoord = aTexCoord;
            gl_Position = uProjection * uView * vec4(aPosition, 1.0);
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec3 vNormal;
        in vec2 vTexCoord;
        in vec3 vWorldPos;

        uniform sampler2D uTexture;
        uniform vec3 uLightDir;  // direction the light travels, normalized
        uniform vec3 uEyePos;

        out vec4 FragColor;

        void main()
        {
            vec3 n = normalize(vNormal);
            vec3 l = normalize(-uLightDir);
            float ndotl = max(dot(n, l), 0.0);

            vec3 viewDir = normalize(uEyePos - vWorldPos);
            vec3 halfDir = normalize(l + viewDir);
            float spec = pow(max(dot(n, halfDir), 0.0), 32.0) * 0.25;

            vec4 texColor = texture(uTexture, vTexCoord);
            vec3 ambient = 0.35 * texColor.rgb;
            vec3 diffuse = 0.75 * ndotl * texColor.rgb;
            vec3 color = ambient + diffuse + vec3(spec);
            FragColor = vec4(color, texColor.a);
        }
        """;

    /// <summary>Escape hatch: flip to true if textures ever appear vertically inverted (see <see cref="UploadTexture"/>).</summary>
    private const bool FlipTextureV = false;

    private readonly IWindow _hiddenWindow;
    private readonly GL _gl;

    private uint _program;
    private int _uView, _uProjection, _uTexture, _uLightDir, _uEyePos;

    private readonly List<MaterialBatch> _batches = [];
    private readonly Dictionary<int, uint> _materialTextures = [];
    private CirMaterial[] _materials = [];

    private uint _fbo, _colorTexture, _depthRenderbuffer;
    private int _fboWidth = -1, _fboHeight = -1;

    public Vector3D<float> BoundsCenter { get; private set; } = Vector3D<float>.Zero;

    public float BoundsRadius { get; private set; } = 1f;

    /// <summary>Background clear colour. Alpha is respected so callers can render onto transparent PNGs.</summary>
    public (float R, float G, float B, float A) ClearColor { get; set; } = (0.16f, 0.16f, 0.18f, 1f);

    /// <summary>When true, draws a wireframe overlay on top of the shaded mesh.</summary>
    public bool ShowWireframe { get; set; }

    public ModelRenderer()
    {
        var options = WindowOptions.Default;
        options.IsVisible = false;
        options.Size = new Vector2D<int>(4, 4);
        options.Title = "TLJ Explorer (hidden GL context)";
        options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(3, 3));
        options.ShouldSwapAutomatically = false;
        options.IsContextControlDisabled = false;

        _hiddenWindow = Window.Create(options);
        _hiddenWindow.Initialize();

        _gl = GL.GetApi(_hiddenWindow);

        _program = CreateProgram();
        _uView = _gl.GetUniformLocation(_program, "uView");
        _uProjection = _gl.GetUniformLocation(_program, "uProjection");
        _uTexture = _gl.GetUniformLocation(_program, "uTexture");
        _uLightDir = _gl.GetUniformLocation(_program, "uLightDir");
        _uEyePos = _gl.GetUniformLocation(_program, "uEyePos");
    }

    private void MakeCurrent() => _hiddenWindow.GLContext?.MakeCurrent();

    // -----------------------------------------------------------------------------------------------
    // Model loading
    // -----------------------------------------------------------------------------------------------

    /// <summary>Floats per interleaved vertex: position(3) + normal(3) + texcoord(2).</summary>
    private const int FloatsPerVertex = 8;

    /// <summary>
    /// Uploads <paramref name="model"/>'s first mesh group as one VAO/VBO/EBO per <see cref="CirFace"/> and
    /// applies the identity pose as the initial default. Materials without a resolved texture get a flat
    /// 1x1 texture from their <see cref="CirMaterial.ColourR"/> et al. until <see cref="SetMaterialTexture"/>
    /// supplies a real one.
    /// </summary>
    public void LoadModel(CirModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        MakeCurrent();

        ClearGeometry();
        ClearMaterialTextures();
        _materials = model.Materials;

        if (model.Groups.Length == 0)
        {
            BoundsCenter = Vector3D<float>.Zero;
            BoundsRadius = 1f;
            return;
        }

        CirGroup group = model.Groups[0];

        foreach (CirFace face in group.Faces)
        {
            uint[] indices = BuildIndexData(face);
            MaterialBatch batch = CreateBatch(face.Vertices, indices, face.MaterialIndex);
            _batches.Add(batch);
        }

        EnsureFallbackMaterialTextures();
        ApplyPose(SkeletonPoser.IdentityPose(model.Skeleton.Length), updateBounds: true);
    }

    /// <summary>
    /// Re-skins every loaded batch against <paramref name="boneWorldMatrices"/> and re-uploads the vertex
    /// buffers in place via <c>glBufferSubData</c>, cheap enough to call once per animation frame.
    /// Set <paramref name="updateBounds"/> only when the caller wants to reframe the camera afterwards;
    /// leaving it false during playback avoids camera jitter as the animation moves the mesh.
    /// </summary>
    public unsafe void ApplyPose(System.Numerics.Matrix4x4[] boneWorldMatrices, bool updateBounds = false)
    {
        ArgumentNullException.ThrowIfNull(boneWorldMatrices);

        if (_batches.Count == 0)
        {
            if (updateBounds)
            {
                BoundsCenter = Vector3D<float>.Zero;
                BoundsRadius = 1f;
            }

            return;
        }

        MakeCurrent();

        var min = new Vector3D<float>(float.MaxValue);
        var max = new Vector3D<float>(float.MinValue);
        bool any = false;

        foreach (MaterialBatch batch in _batches)
        {
            float[] vertexData = BuildVertexData(batch.Vertices, boneWorldMatrices, ref min, ref max, ref any);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, batch.Vbo);
            _gl.BufferSubData<float>(BufferTargetARB.ArrayBuffer, IntPtr.Zero, new ReadOnlySpan<float>(vertexData));
        }

        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);

        if (!updateBounds)
            return;

        if (any)
        {
            BoundsCenter = (min + max) * 0.5f;
            float dx = max.X - min.X, dy = max.Y - min.Y, dz = max.Z - min.Z;
            BoundsRadius = MathF.Max(0.01f, MathF.Sqrt(dx * dx + dy * dy + dz * dz) * 0.5f);
        }
        else
        {
            BoundsCenter = Vector3D<float>.Zero;
            BoundsRadius = 1f;
        }
    }

    /// <summary>Skins each vertex's two bone-local positions by their bone matrices and blends by
    /// <see cref="CirVertex.BoneWeight"/>. Normals use the rotation-only transform (TransformNormal).</summary>
    private static float[] BuildVertexData(
        CirVertex[] vertices,
        System.Numerics.Matrix4x4[] boneWorldMatrices,
        ref Vector3D<float> min,
        ref Vector3D<float> max,
        ref bool any)
    {
        var data = new float[vertices.Length * FloatsPerVertex];

        for (int i = 0; i < vertices.Length; i++)
        {
            CirVertex v = vertices[i];

            System.Numerics.Matrix4x4 m1 = BoneMatrixOrIdentity(boneWorldMatrices, v.BoneIndex1);
            System.Numerics.Matrix4x4 m2 = BoneMatrixOrIdentity(boneWorldMatrices, v.BoneIndex2);

            var localPos1 = new System.Numerics.Vector3(v.PosX1, v.PosY1, v.PosZ1);
            var localPos2 = new System.Numerics.Vector3(v.PosX2, v.PosY2, v.PosZ2);
            System.Numerics.Vector3 worldPos1 = System.Numerics.Vector3.Transform(localPos1, m1);
            System.Numerics.Vector3 worldPos2 = System.Numerics.Vector3.Transform(localPos2, m2);
            System.Numerics.Vector3 pos = (worldPos1 * v.BoneWeight) + (worldPos2 * (1f - v.BoneWeight));

            var localNormal = new System.Numerics.Vector3(v.NormalX, v.NormalY, v.NormalZ);
            System.Numerics.Vector3 worldNormal1 = System.Numerics.Vector3.TransformNormal(localNormal, m1);
            System.Numerics.Vector3 worldNormal2 = System.Numerics.Vector3.TransformNormal(localNormal, m2);
            System.Numerics.Vector3 normal = (worldNormal1 * v.BoneWeight) + (worldNormal2 * (1f - v.BoneWeight));
            if (normal.LengthSquared() > 1e-12f)
                normal = System.Numerics.Vector3.Normalize(normal);

            // Sanitize NaN/Infinity and finite-but-enormous positions before they reach the GPU: a
            // pathological triangle spanning an astronomical extent can trip Windows' TDR watchdog and
            // crash the GL context. Can happen when an .ani clip is sampled against a skeleton it wasn't
            // authored for.
            const float MaxSanePositionMagnitude = 100_000f; // Real character meshes are single-digit units.
            bool posOutOfRange = IsFinite(pos) && pos.LengthSquared() > MaxSanePositionMagnitude * MaxSanePositionMagnitude;
            if (!IsFinite(pos) || !IsFinite(normal) || posOutOfRange)
            {
                pos = System.Numerics.Vector3.Zero;
                normal = System.Numerics.Vector3.UnitY;
            }

            int o = i * FloatsPerVertex;
            data[o + 0] = pos.X;
            data[o + 1] = pos.Y;
            data[o + 2] = pos.Z;
            data[o + 3] = normal.X;
            data[o + 4] = normal.Y;
            data[o + 5] = normal.Z;
            data[o + 6] = v.TextureS;
            data[o + 7] = v.TextureT;

            var p = new Vector3D<float>(pos.X, pos.Y, pos.Z);
            min = Vector3D.Min(min, p);
            max = Vector3D.Max(max, p);
            any = true;
        }

        return data;
    }

    private static bool IsFinite(System.Numerics.Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Falls back to identity for out-of-range bone indices (defensive against malformed data).</summary>
    private static System.Numerics.Matrix4x4 BoneMatrixOrIdentity(System.Numerics.Matrix4x4[] boneWorldMatrices, int boneIndex) =>
        boneIndex >= 0 && boneIndex < boneWorldMatrices.Length ? boneWorldMatrices[boneIndex] : System.Numerics.Matrix4x4.Identity;

    private static uint[] BuildIndexData(CirFace face)
    {
        var indices = new uint[face.Triangles.Length * 3];
        for (int i = 0; i < face.Triangles.Length; i++)
        {
            CirTriangle t = face.Triangles[i];
            indices[(i * 3) + 0] = (uint)t.VertexIndex1;
            indices[(i * 3) + 1] = (uint)t.VertexIndex2;
            indices[(i * 3) + 2] = (uint)t.VertexIndex3;
        }

        return indices;
    }

    private unsafe MaterialBatch CreateBatch(CirVertex[] vertices, uint[] indices, int materialIndex)
    {
        uint vao = _gl.GenVertexArray();
        _gl.BindVertexArray(vao);

        uint vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

        // Allocate storage only; the real vertex data is filled in by ApplyPose. DynamicDraw because the
        // buffer is re-uploaded every animation frame during playback.
        var placeholder = new float[vertices.Length * FloatsPerVertex];
        _gl.BufferData<float>(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(placeholder), BufferUsageARB.DynamicDraw);

        uint ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        _gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(indices), BufferUsageARB.StaticDraw);

        const uint stride = FloatsPerVertex * sizeof(float);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);

        _gl.BindVertexArray(0);

        return new MaterialBatch(vao, vbo, ebo, (uint)indices.Length, materialIndex, vertices);
    }

    private void ClearGeometry()
    {
        foreach (MaterialBatch batch in _batches)
        {
            _gl.DeleteVertexArray(batch.Vao);
            _gl.DeleteBuffer(batch.Vbo);
            _gl.DeleteBuffer(batch.Ebo);
        }

        _batches.Clear();
    }

    // -----------------------------------------------------------------------------------------------
    // Material textures
    // -----------------------------------------------------------------------------------------------

    private void EnsureFallbackMaterialTextures()
    {
        for (int i = 0; i < _materials.Length; i++)
        {
            if (_materialTextures.ContainsKey(i))
                continue;

            _materialTextures[i] = CreateFlatColorTexture(_materials[i]);
        }
    }

    /// <summary>
    /// Supplies (or replaces) the real texture resolved for a material, e.g. from a chosen skin's matching
    /// named entry (see <c>MainWindow.ApplySkinAsync</c>). Passing <c>null</c> leaves whatever texture
    /// (real or flat-color fallback) is already in place for this material untouched.
    /// </summary>
    public void SetMaterialTexture(int materialIndex, DecodedImage? image)
    {
        if (image is null)
            return;

        MakeCurrent();

        if (_materialTextures.TryGetValue(materialIndex, out uint existing))
        {
            _gl.DeleteTexture(existing);
        }

        _materialTextures[materialIndex] = UploadTexture(image);
    }

    /// <summary>Reverts a material back to its flat-color solid fill.</summary>
    public void ResetMaterialTexture(int materialIndex)
    {
        if (materialIndex < 0 || materialIndex >= _materials.Length)
            return;

        MakeCurrent();

        if (_materialTextures.TryGetValue(materialIndex, out uint existing))
        {
            _gl.DeleteTexture(existing);
        }

        _materialTextures[materialIndex] = CreateFlatColorTexture(_materials[materialIndex]);
    }

    /// <summary>Reverts every material back to its flat-color fallback (the "(none)" skin choice).</summary>
    public void ResetMaterialTextures()
    {
        MakeCurrent();
        ClearMaterialTextures();
        EnsureFallbackMaterialTextures();
    }

    private unsafe uint CreateFlatColorTexture(CirMaterial material)
    {
        // Handle either [0,1] float or [0,255] byte-scaled color values.
        float scale = MathF.Max(material.ColourR, MathF.Max(material.ColourG, material.ColourB)) > 1.5f ? 1f / 255f : 1f;

        byte r = (byte)(Math.Clamp(material.ColourR * scale, 0f, 1f) * 255f);
        byte g = (byte)(Math.Clamp(material.ColourG * scale, 0f, 1f) * 255f);
        byte b = (byte)(Math.Clamp(material.ColourB * scale, 0f, 1f) * 255f);

        Span<byte> pixel = [b, g, r, 255]; // BGRA to match UploadTexture's channel order below
        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = pixel)
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    private unsafe uint UploadTexture(DecodedImage image)
    {
        // DecodedImage.Pixels is BGRA32, top-down. Uploaded unflipped so row 0 lands at v=0, matching the
        // v=0-at-top texture convention this game's engine appears to use. Flip via FlipTextureV if wrong.
        byte[] pixels = image.Pixels;
#pragma warning disable CS0162 // FlipTextureV is a compile-time toggle.
        if (FlipTextureV)
        {
            pixels = FlipRows(pixels, image.Width, image.Height);
        }
#pragma warning restore CS0162

        uint tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D, 0, InternalFormat.Rgba8,
                (uint)image.Width, (uint)image.Height, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, p);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.Repeat);
        return tex;
    }

    private static byte[] FlipRows(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        var flipped = new byte[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
        }

        return flipped;
    }

    private void ClearMaterialTextures()
    {
        foreach (uint tex in _materialTextures.Values)
        {
            _gl.DeleteTexture(tex);
        }

        _materialTextures.Clear();
    }

    // -----------------------------------------------------------------------------------------------
    // Rendering
    // -----------------------------------------------------------------------------------------------

    /// <summary>Renders the current model at the given size and returns a top-down BGRA32 pixel buffer.</summary>
    public unsafe byte[] RenderFrame(int width, int height, OrbitCamera camera)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        MakeCurrent();
        EnsureFramebuffer(width, height);

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, (uint)width, (uint)height);

        _gl.ClearColor(ClearColor.R, ClearColor.G, ClearColor.B, ClearColor.A);
        _gl.Enable(EnableCap.DepthTest);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (_batches.Count > 0)
        {
            _gl.UseProgram(_program);

            float aspect = width / (float)height;
            float near = MathF.Max(0.01f, BoundsRadius * 0.02f);
            float far = MathF.Max(near + 1f, BoundsRadius * 20f);
            Matrix4X4<float> projection = Matrix4X4.CreatePerspectiveFieldOfView(MathF.PI / 4f, aspect, near, far);
            Matrix4X4<float> view = Matrix4X4.CreateLookAt(camera.Eye, camera.Target, Vector3D<float>.UnitY);

            _gl.UniformMatrix4(_uProjection, 1, false, (float*)&projection);
            _gl.UniformMatrix4(_uView, 1, false, (float*)&view);
            _gl.Uniform3(_uEyePos, camera.Eye.X, camera.Eye.Y, camera.Eye.Z);

            // Fixed key light from up-front, combined with the ambient term in the fragment shader.
            var lightDir = Vector3D.Normalize(new Vector3D<float>(-0.4f, -0.8f, -0.4f));
            _gl.Uniform3(_uLightDir, lightDir.X, lightDir.Y, lightDir.Z);

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.Uniform1(_uTexture, 0);

            foreach (MaterialBatch batch in _batches)
            {
                uint texture = _materialTextures.TryGetValue(batch.MaterialIndex, out uint t) ? t : 0;
                _gl.BindTexture(TextureTarget.Texture2D, texture);
                _gl.BindVertexArray(batch.Vao);
                _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }

            if (ShowWireframe)
            {
                // Second pass: draw the same geometry as unlit lines using polygon-mode LINE. A small depth
                // offset pushes the lines toward the camera so they don't z-fight the shaded surface.
                _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                _gl.Enable(EnableCap.PolygonOffsetLine);
                _gl.PolygonOffset(-1f, -1f);

                // Bind a 1x1 white texture-substitute by using the fragment shader's default when nothing is
                // bound; we don't have one, so bind whatever texture is convenient. The lit shader will
                // sample a mostly-uniform colour, which is fine for wireframe purposes.
                foreach (MaterialBatch batch in _batches)
                {
                    _gl.BindVertexArray(batch.Vao);
                    _gl.DrawElements(PrimitiveType.Triangles, batch.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
                }

                _gl.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
                _gl.Disable(EnableCap.PolygonOffsetLine);
            }

            _gl.BindVertexArray(0);
        }

        // glReadPixels returns rows bottom-up; WriteableBitmap/DecodedImage want top-down, so flip here.
        var raw = new byte[width * height * 4];
        fixed (byte* p = raw)
        {
            _gl.ReadPixels(0, 0, (uint)width, (uint)height, PixelFormat.Bgra, PixelType.UnsignedByte, p);
        }

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        return FlipRows(raw, width, height);
    }

    private void EnsureFramebuffer(int width, int height)
    {
        if (width == _fboWidth && height == _fboHeight && _fbo != 0)
            return;

        DeleteFramebufferResources();

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);

        _colorTexture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, _colorTexture);
        unsafe
        {
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _colorTexture, 0);

        _depthRenderbuffer = _gl.GenRenderbuffer();
        _gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depthRenderbuffer);
        _gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, (uint)width, (uint)height);
        _gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, _depthRenderbuffer);

        GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
        {
            throw new InvalidOperationException($"OpenGL framebuffer incomplete: {status}.");
        }

        _fboWidth = width;
        _fboHeight = height;
    }

    private void DeleteFramebufferResources()
    {
        if (_fbo != 0) _gl.DeleteFramebuffer(_fbo);
        if (_colorTexture != 0) _gl.DeleteTexture(_colorTexture);
        if (_depthRenderbuffer != 0) _gl.DeleteRenderbuffer(_depthRenderbuffer);
        _fbo = _colorTexture = _depthRenderbuffer = 0;
    }

    // -----------------------------------------------------------------------------------------------
    // Shader setup
    // -----------------------------------------------------------------------------------------------

    private uint CreateProgram()
    {
        uint vs = CompileShader(ShaderType.VertexShader, VertexShaderSource);
        uint fs = CompileShader(ShaderType.FragmentShader, FragmentShaderSource);

        uint program = _gl.CreateProgram();
        _gl.AttachShader(program, vs);
        _gl.AttachShader(program, fs);
        _gl.LinkProgram(program);

        _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linkStatus);
        if (linkStatus == 0)
        {
            string log = _gl.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Failed to link model shader program: {log}");
        }

        _gl.DetachShader(program, vs);
        _gl.DetachShader(program, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);

        return program;
    }

    private uint CompileShader(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);

        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compileStatus);
        if (compileStatus == 0)
        {
            string log = _gl.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Failed to compile {type}: {log}");
        }

        return shader;
    }

    // -----------------------------------------------------------------------------------------------

    public void Dispose()
    {
        // The hidden GLFW window MUST be disposed no matter what — its native message loop runs on
        // a foreground thread and will keep the whole process alive if left behind (that's how you
        // end up with orphaned TLJExplorer.exe instances after a crash on shutdown). Guard every
        // earlier step and always land on _hiddenWindow.Dispose() in the finally.
        try
        {
            try { MakeCurrent(); } catch { /* context may already be gone */ }
            try { ClearGeometry(); } catch { }
            try { ClearMaterialTextures(); } catch { }
            try { DeleteFramebufferResources(); } catch { }

            if (_program != 0)
            {
                try { _gl.DeleteProgram(_program); } catch { }
                _program = 0;
            }

            try { _gl.Dispose(); } catch { }
        }
        finally
        {
            try { _hiddenWindow.Dispose(); } catch { }
        }
    }

    /// <summary><see cref="Vertices"/> is the un-skinned source data, kept for re-posing on demand.</summary>
    private readonly record struct MaterialBatch(uint Vao, uint Vbo, uint Ebo, uint IndexCount, int MaterialIndex, CirVertex[] Vertices);
}
