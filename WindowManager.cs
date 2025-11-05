using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Rampastring.XNAUI.XNAControls;
using Rampastring.Tools;
using Microsoft.Xna.Framework.Content;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;
using Rampastring.XNAUI.Input;
using System.Diagnostics;
using System.Linq;
using Rampastring.XNAUI.PlatformSpecific;
#if NETFRAMEWORK
using System.Reflection;
#endif
#if WINFORMS
using System.Windows.Forms;
#endif

namespace Rampastring.XNAUI;

/// <summary>
/// Manages the game window and all of the game's controls
/// inside the game window.
/// </summary>
public class WindowManager : DrawableGameComponent
{
    private const int XNA_MAX_TEXTURE_SIZE = 2048;

    /// <summary>
    /// Creates a new WindowManager.
    /// </summary>
    /// <param name="game">The game.</param>
    /// <param name="graphics">The game's GraphicsDeviceManager.</param>
    public WindowManager(Game game, GraphicsDeviceManager graphics) : base(game)
    {
        this.graphics = graphics;
    }

    /// <summary>
    /// Raised when the game window is closing.
    /// </summary>
    public event EventHandler GameClosing;

    /// <summary>
    /// Raised when the render resolution is changed.
    /// </summary>
    public event EventHandler RenderResolutionChanged;

#if WINFORMS
    /// <summary>
    /// Raised when the size of the game window has been changed by the user or the operating system.
    /// This event is not raised by calling <see cref="InitGraphicsMode(int, int, bool)"/>.
    /// </summary>
    public event EventHandler WindowSizeChangedByUser;
#endif

    /// <summary>
    /// The input cursor.
    /// </summary>
    public Input.Cursor Cursor { get; private set; }

    /// <summary>
    /// The keyboard.
    /// </summary>
    public RKeyboard Keyboard { get; private set; }

    /// <summary>
    /// The SoundPlayer that is responsible for handling audio.
    /// </summary>
    public SoundPlayer SoundPlayer { get; private set; }

    private List<XNAControl> Controls = new List<XNAControl>();

    private List<Callback> Callbacks = new List<Callback>();

    private readonly object locker = new object();

    /// <summary>
    /// Returns the width of the game window, including the window borders.
    /// </summary>
    public int WindowWidth { get; private set; } = 800;

    /// <summary>
    /// Returns the height of the game window, including the window borders.
    /// </summary>
    public int WindowHeight { get; private set; } = 600;

    /// <summary>
    /// Returns the width of the back buffer.
    /// </summary>
    public int RenderResolutionX { get; private set; } = 800;

    /// <summary>
    /// Returns the height of the back buffer.
    /// </summary>
    public int RenderResolutionY { get; private set; } = 600;

    /// <summary>
    /// Gets a boolean that determines whether the game window currently has input focus.
    /// </summary>
    public bool HasFocus { get; private set; } = true;

    public double ScaleRatio { get; private set; } = 1.0;

    public int SceneXPosition { get; private set; } = 0;
    public int SceneYPosition { get; private set; } = 0;

    private XNAControl _selectedControl;

    /// <summary>
    /// Gets or sets the control that is currently selected.
    /// Usually used for controls that need input focus, like text boxes.
    /// </summary>
    public XNAControl SelectedControl
    {
        get { return _selectedControl; }
        set
        {
            XNAControl oldSelectedControl = _selectedControl;
            _selectedControl = value;

            if (oldSelectedControl != _selectedControl)
            {
                if (_selectedControl != null)
                    _selectedControl.OnSelectedChanged();

                if (oldSelectedControl != null)
                    oldSelectedControl.OnSelectedChanged();
            }
        }
    }

    /// <summary>
    /// Returns a bool that determines whether input is
    /// currently exclusively captured by the selected control.
    /// </summary>
    public bool IsInputExclusivelyCaptured => SelectedControl != null && SelectedControl.ExclusiveInputCapture;

    /// <summary>
    /// A list of custom control INI attribute parsers.
    /// Allows extending the control INI attribute parsing
    /// system with custom INI keys.
    /// </summary>
    public List<IControlINIAttributeParser> ControlINIAttributeParsers { get; private set; } = new List<IControlINIAttributeParser>();

    /// <summary>
    /// If set, only scales the rendered screen by integer scaling factors. Unfilled space is filled with black.
    /// </summary>
    public bool IntegerScalingOnly { get; set; }

    /// <summary>
    /// Object for handling Input Method Editor (IME) based input.
    /// </summary>
    public IIMEHandler IMEHandler { get; set; } = null;

    /// <summary>
    /// The control, of the highest generation, that the mouse cursor is currently positioned on.
    /// </summary>
    internal XNAControl ActiveControl { get; set; }

#if DEBUG
    /// <summary>
    /// Editor mode allows dragging and resizing controls.
    /// Toggle with F11 key.
    /// </summary>
    public bool EditorMode { get; set; } = false;

    /// <summary>
    /// Controls whether the debug panel is visible.
    /// </summary>
    public bool ShowDebugPanel { get; set; } = true;

    private XNAControl draggedControl = null;
    private Point dragOffset;
    private ResizeMode resizeMode = ResizeMode.None;
    private Rectangle originalControlBounds;
    private Point resizeStartCursorPos;

    // Debug panel
    private Point debugPanelPosition = new Point(0, 0);
    private bool isDraggingDebugPanel = false;
    private Point debugPanelDragOffset;

    private enum ResizeMode
    {
        None,
        Move,
        ResizeLeft,
        ResizeRight,
        ResizeTop,
        ResizeBottom,
        ResizeTopLeft,
        ResizeTopRight,
        ResizeBottomLeft,
        ResizeBottomRight
    }
#endif

    private GraphicsDeviceManager graphics;

    private IGameWindowManager gameWindowManager;
    private RenderTarget2D renderTarget;
    private RenderTarget2D doubledRenderTarget;

    /// <summary>
    /// Sets the rendering (back buffer) resolution of the game.
    /// Does not affect the size of the actual game window.
    /// </summary>
    /// <param name="x">The width of the back buffer.</param>
    /// <param name="y">The height of the back buffer.</param>
    public void SetRenderResolution(int x, int y)
    {
#if XNA
        x = Math.Min(x, XNA_MAX_TEXTURE_SIZE);
        y = Math.Min(y, XNA_MAX_TEXTURE_SIZE);
#endif

        RenderResolutionX = x;
        RenderResolutionY = y;

        RecalculateScaling();
        RenderResolutionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Re-calculates the scaling of the rendered screen to fill the window.
    /// </summary>
    private void RecalculateScaling()
    {
        int clientAreaWidth = Game.Window.ClientBounds.Width;
        int clientAreaHeight = Game.Window.ClientBounds.Height;

        double xRatio = clientAreaWidth / (double)RenderResolutionX;
        double yRatio = clientAreaHeight / (double)RenderResolutionY;

        if (IntegerScalingOnly && clientAreaWidth >= RenderResolutionX && clientAreaHeight >= RenderResolutionY)
        {
            xRatio = clientAreaWidth / RenderResolutionX;
            yRatio = clientAreaHeight / RenderResolutionY;
        }

        double ratio;

        int texturePositionX = 0;
        int texturePositionY = 0;

        if (xRatio > yRatio)
        {
            ratio = yRatio;
            int textureWidth = (int)(RenderResolutionX * ratio);
            texturePositionX = (clientAreaWidth - textureWidth) / 2;
            if (IntegerScalingOnly)
            {
                int textureHeight = (int)(RenderResolutionY * ratio);
                texturePositionY = (clientAreaHeight - textureHeight) / 2;
            }
        }
        else
        {
            ratio = xRatio;
            int textureHeight = (int)(RenderResolutionY * ratio);
            texturePositionY = (clientAreaHeight - textureHeight) / 2;
            if (IntegerScalingOnly)
            {
                int textureWidth = (int)(RenderResolutionX * ratio);
                texturePositionX = (clientAreaWidth - textureWidth) / 2;
            }
        }

        ScaleRatio = ratio;
        SceneXPosition = texturePositionX;
        SceneYPosition = texturePositionY;

        if (renderTarget != null && !renderTarget.IsDisposed)
            renderTarget.Dispose();

        if (doubledRenderTarget != null && !doubledRenderTarget.IsDisposed)
            doubledRenderTarget.Dispose();

        renderTarget = new RenderTarget2D(GraphicsDevice, RenderResolutionX, RenderResolutionY, false, SurfaceFormat.Color,
            DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

        RenderTargetStack.Initialize(renderTarget, GraphicsDevice);
        RenderTargetStack.InitDetachedScaledControlRenderTarget(RenderResolutionX, RenderResolutionY);

        if (ScaleRatio > 1.5 && ScaleRatio % 1.0 == 0)
        {
#if XNA
            if (RenderResolutionX * 2 > XNA_MAX_TEXTURE_SIZE || RenderResolutionY * 2 > XNA_MAX_TEXTURE_SIZE)
            {
                doubledRenderTarget = null;
                return;
            }
#endif

            // Enable sharper scaling method
            doubledRenderTarget = new RenderTarget2D(GraphicsDevice,
                RenderResolutionX * 2, RenderResolutionY * 2, false, SurfaceFormat.Color,
                DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        }
        else
        {
            doubledRenderTarget = null;
        }
    }

    /// <summary>
    /// Closes the game.
    /// </summary>
    public void CloseGame()
    {
#if !WINFORMS
        // When using UniversalGL both GameClosing and Game.Exiting trigger GameWindowManager_GameWindowClosing().
        // To avoid executing shutdown code twice we unsubscribe here from Game.Exiting.
        // The default double subscription needs to stay to handle the case of a forceful shutdown e.g. alt+F4.
        Game.Exiting -= GameWindowManager_GameWindowClosing;
#endif
        GameClosing?.Invoke(this, EventArgs.Empty);
        Game.Exit();
    }

    /// <summary>
    /// Restarts the game.
    /// </summary>
    public void RestartGame()
    {
        Logger.Log("Restarting game.");

#if !XNA
        // MonoGame takes ages to unload assets compared to XNA; sometimes MonoGame
        // can take over 8 seconds while XNA takes only 1 second
        // This is a bit dirty, but at least it makes the MonoGame build exit quicker
        GameClosing?.Invoke(this, EventArgs.Empty);
        // TODO move Windows-specific functionality
#if WINFORMS
        Application.DoEvents();
#endif
#if NETFRAMEWORK
        using var process = Process.Start(Assembly.GetEntryAssembly().Location);
#else
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath,
            Arguments = Environment.CommandLine
        });
#endif

        Environment.Exit(0);
#else
        Application.Restart();
#endif
    }

    /// <summary>
    /// Initializes the WindowManager.
    /// </summary>
    /// <param name="content">The game content manager.</param>
    /// <param name="contentPath">The path where the ContentManager should load files from (including SpriteFont files).</param>
    public void Initialize(ContentManager content, string contentPath)
    {
        base.Initialize();

        content.RootDirectory = SafePath.GetDirectory(contentPath).FullName;

        Cursor = new Input.Cursor(this);
        Cursor.Initialize();
        Keyboard = new RKeyboard(Game);
        if (!AssetLoader.IsInitialized)
            AssetLoader.Initialize(graphics.GraphicsDevice, content);
        Renderer.Initialize(GraphicsDevice, content);
        SoundPlayer = new SoundPlayer(Game);

        gameWindowManager = new WindowsGameWindowManager(Game);
#if WINFORMS
        gameWindowManager.GameWindowClosing += GameWindowManager_GameWindowClosing;
        gameWindowManager.ClientSizeChanged += GameWindowManager_ClientSizeChanged;
#else
        Game.Exiting += GameWindowManager_GameWindowClosing;
#endif

        if (UISettings.ActiveSettings == null)
            UISettings.ActiveSettings = new UISettings();
#if XNA

        KeyboardEventInput.Initialize(Game.Window);
#endif
    }

#if WINFORMS
    private void GameWindowManager_ClientSizeChanged(object sender, EventArgs e)
    {
        WindowWidth = gameWindowManager.GetWindowWidth();
        WindowHeight = gameWindowManager.GetWindowHeight();
        RecalculateScaling();
        WindowSizeChangedByUser?.Invoke(this, EventArgs.Empty);
    }
#endif

    private void GameWindowManager_GameWindowClosing(object sender, EventArgs e)
    {
        GameClosing?.Invoke(this, EventArgs.Empty);
    }

#if WINFORMS
    /// <summary>
    /// Sets the border style of the game form.
    /// Throws an exception if the application is running in borderless mode.
    /// </summary>
    /// <param name="formBorderStyle">The form border style to apply.</param>
    public void SetFormBorderStyle(FormBorderStyle formBorderStyle)
    {
        gameWindowManager.SetFormBorderStyle(formBorderStyle);
    }
#endif

    /// <summary>
    /// Schedules a delegate to be executed on the next game loop frame, 
    /// on the main game thread.
    /// </summary>
    /// <param name="d">The delegate.</param>
    /// <param name="args">The arguments to be passed on to the delegate.</param>
    public void AddCallback(Delegate d, params object[] args)
    {
        lock (locker)
            Callbacks.Add(new Callback(d, args));
    }

    /// <summary>
    /// Adds a control into the WindowManager, on the last place
    /// in the list of controls.
    /// </summary>
    /// <param name="control">The control to add.</param>
    public void AddAndInitializeControl(XNAControl control)
    {
        if (Controls.Contains(control))
        {
            throw new InvalidOperationException("WindowManager.AddAndInitializeControl: Control " + control.Name + " already exists!");
        }

        control.Initialize();
        Controls.Add(control);
        ReorderControls();
    }

    /// <summary>
    /// Adds a control to the WindowManager, on the last place
    /// in the list of controls. Does not call the control's
    /// Initialize() method.
    /// </summary>
    /// <param name="control">The control to add.</param>
    public void AddControl(XNAControl control)
    {
        if (Controls.Contains(control))
        {
            throw new InvalidOperationException("WindowManager.AddControl: Control " + control.Name + " already exists!");
        }

        Controls.Add(control);
    }

    /// <summary>
    /// Inserts a control into the WindowManager on the first place
    /// in the list of controls.
    /// </summary>
    /// <param name="control">The control to insert.</param>
    public void InsertAndInitializeControl(XNAControl control)
    {
        if (Controls.Contains(control))
        {
            throw new Exception("WindowManager.InsertAndInitializeControl: Control " + control.Name + " already exists!");
        }

        Controls.Insert(0, control);
    }

    /// <summary>
    /// Centers a control on the game window.
    /// </summary>
    /// <param name="control">The control to center.</param>
    public void CenterControlOnScreen(XNAControl control)
    {
        control.ClientRectangle = new Rectangle((RenderResolutionX - control.Width) / 2,
            (RenderResolutionY - control.Height) / 2, control.Width, control.Height);
    }

    /// <summary>
    /// Centers the game window on the screen.
    /// </summary>
    public void CenterOnScreen()
    {
        gameWindowManager.CenterOnScreen();
    }

    /// <summary>
    /// Enables or disables borderless windowed mode.
    /// </summary>
    /// <param name="value">A boolean that determines whether borderless
    /// windowed mode should be enabled.</param>
    public void SetBorderlessMode(bool value)
    {
        gameWindowManager.SetBorderlessMode(value);
    }

#if WINFORMS
    public void MinimizeWindow()
    {
        gameWindowManager.MinimizeWindow();
    }

    public void MaximizeWindow()
    {
        gameWindowManager.MaximizeWindow();
    }

    public void HideWindow()
    {
        gameWindowManager.HideWindow();
    }

    public void ShowWindow()
    {
        gameWindowManager.ShowWindow();
    }

    /// <summary>
    /// Flashes the game window on the taskbar.
    /// </summary>
    public void FlashWindow()
    {
        gameWindowManager.FlashWindow();
    }

    /// <summary>
    /// Sets the icon of the game window to an icon that exists on a specific
    /// file path.
    /// </summary>
    /// <param name="path">The path to the icon file.</param>
    public void SetIcon(string path)
    {
        gameWindowManager.SetIcon(path);
    }

    /// <summary>
    /// Returns the IntPtr handle of the game window on Windows.
    /// On other platforms, returns IntPtr.Zero.
    /// </summary>
    public IntPtr GetWindowHandle()
    {
        return gameWindowManager.GetWindowHandle();
    }

    /// <summary>
    /// Enables or disables the "maximize box" for the game form.
    /// </summary>
    public void SetMaximizeBox(bool value) => gameWindowManager.SetMaximizeBox(value);

    /// <summary>
    /// Enables or disables the "control box" (minimize/maximize/close buttons) for the game form.
    /// </summary>
    /// <param name="value">True to enable the control box, false to disable it.</param>
    public void SetControlBox(bool value)
    {
        gameWindowManager.SetControlBox(value);
    }

    /// <summary>
    /// Prevents the user from closing the game form by Alt-F4.
    /// </summary>
    public void PreventClosing()
    {
        gameWindowManager.PreventClosing();
    }

    /// <summary>
    /// Allows the user to close the game form by Alt-F4.
    /// </summary>
    public void AllowClosing()
    {
        gameWindowManager.AllowClosing();
    }

#endif
    /// <summary>
    /// Removes a control from the window manager.
    /// </summary>
    /// <param name="control">The control to remove.</param>
    public void RemoveControl(XNAControl control)
    {
        Controls.Remove(control);
    }

    /// <summary>
    /// Enables or disables VSync.
    /// </summary>
    /// <param name="value">A boolean that determines whether VSync should be enabled or disabled.</param>
    public void SetVSync(bool value)
    {
        graphics.SynchronizeWithVerticalRetrace = value;
    }

    public void SetFinalRenderTarget()
    {
        GraphicsDevice.SetRenderTarget(renderTarget);
    }

    public RenderTarget2D GetFinalRenderTarget()
    {
        return renderTarget;
    }

    /// <summary>
    /// Re-orders controls by their update order.
    /// </summary>
    public void ReorderControls()
    {
        Controls = Controls.OrderBy(control => control.Detached).ThenBy(control => control.UpdateOrder).ToList();
    }

    /// <summary>
    /// Attempt to set the display mode to the desired resolution.  Itterates through the display
    /// capabilities of the default graphics adapter to determine if the graphics adapter supports the
    /// requested resolution.  If so, the resolution is set and the function returns true.  If not,
    /// no change is made and the function returns false.
    /// </summary>
    /// <param name="iWidth">Desired screen width.</param>
    /// <param name="iHeight">Desired screen height.</param>
    /// <param name="bFullScreen">True if you wish to go to Full Screen, false for Windowed Mode.</param>
    public bool InitGraphicsMode(int iWidth, int iHeight, bool bFullScreen)
    {
        Logger.Log("InitGraphicsMode: " + iWidth + "x" + iHeight);
        WindowWidth = iWidth;
        WindowHeight = iHeight;
        // If we aren't using a full screen mode, the height and width of the window can
        // be set to anything equal to or smaller than the actual screen size.
        if (bFullScreen == false)
        {
            if ((iWidth <= GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width)
                && (iHeight <= GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height))
            {
#if WINFORMS
                gameWindowManager.ClientSizeChanged -= GameWindowManager_ClientSizeChanged;
#endif

                graphics.PreferredBackBufferWidth = iWidth;
                graphics.PreferredBackBufferHeight = iHeight;
                graphics.IsFullScreen = bFullScreen;
                graphics.ApplyChanges();
                RecalculateScaling();

#if WINFORMS
                gameWindowManager.ClientSizeChanged += GameWindowManager_ClientSizeChanged;
#endif

                return true;
            }
        }
        else
        {
            // If we are using full screen mode, we should check to make sure that the display
            // adapter can handle the video mode we are trying to set.  To do this, we will
            // iterate thorugh the display modes supported by the adapter and check them against
            // the mode we want to set.
            foreach (DisplayMode dm in GraphicsAdapter.DefaultAdapter.SupportedDisplayModes)
            {
                // Check the width and height of each mode against the passed values
                if ((dm.Width == iWidth) && (dm.Height == iHeight))
                {
                    // The mode is supported, so set the buffer formats, apply changes and return

#if WINFORMS
                    gameWindowManager.ClientSizeChanged -= GameWindowManager_ClientSizeChanged;
#endif

                    graphics.PreferredBackBufferWidth = iWidth;
                    graphics.PreferredBackBufferHeight = iHeight;
                    graphics.IsFullScreen = bFullScreen;
                    graphics.ApplyChanges();
                    RecalculateScaling();

#if WINFORMS
                    gameWindowManager.ClientSizeChanged += GameWindowManager_ClientSizeChanged;
#endif

                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether the game is running in fullscreen mode.
    /// </summary>
    public bool IsFullscreen => graphics.IsFullScreen;

    /// <summary>
    /// Updates the WindowManager. Do not call manually; MonoGame will call 
    /// this automatically on every game frame.
    /// </summary>
    /// <param name="gameTime">Provides a snapshot of timing values.</param>
    public override void Update(GameTime gameTime)
    {
        HasFocus = gameWindowManager.HasFocus();

        lock (locker)
        {
            if (Callbacks.Count > 0)
            {
                List<Callback> callbacks = Callbacks;
                Callbacks = new List<Callback>();

                foreach (Callback c in callbacks)
                    c.Invoke();
            }
        }

        if (HasFocus)
            Keyboard.Update(gameTime);

        Cursor.Update(gameTime);

        SoundPlayer.Update(gameTime);

#if DEBUG
        // F11 cycles through view, edit, hidden
        if (Keyboard.PressedKeys.Contains(Microsoft.Xna.Framework.Input.Keys.F11))
        {
            if (!EditorMode && ShowDebugPanel)
            {
                EditorMode = true;
            }
            else if (EditorMode && ShowDebugPanel)
            {
                EditorMode = false;
                ShowDebugPanel = false;
                draggedControl = null;
            }
            else
            {
                ShowDebugPanel = true;
            }
        }

        if (ShowDebugPanel)
            HandleDebugPanelDragging();

        if (EditorMode)
            HandleControlDragging();

        if (iniCopiedNotificationTime > TimeSpan.Zero)
            iniCopiedNotificationTime -= gameTime.ElapsedGameTime;
#endif

        UpdateControls(gameTime);

        base.Update(gameTime);
    }

    private void UpdateControls(GameTime gameTime)
    {
        ActiveControl = null;

        for (int i = Controls.Count - 1; i > -1; i--)
        {
            XNAControl control = Controls[i];

            // Before calling the control's Update, check whether the control is currently under the mouse cursor.
            if (HasFocus && control.InputEnabled && control.Enabled &&
                (ActiveControl == null && control.GetWindowRectangle().Contains(Cursor.Location) || control.Focused))
            {
                ActiveControl = control;
            }

            if (control.Enabled)
            {
                control.Update(gameTime);

                // In case ActiveControl points to the control after its Update routine has been called,
                // that means that none of the control's children handled input.
                // In case the control is InputPassthrough, clear the active control to give
                // underlying controls a chance to handle input instead.
                // Also check for the control's children to enable children to be InputPassthrough.
                if (ActiveControl != null && ActiveControl.InputPassthrough && (ActiveControl == control || control.IsParentOf(ActiveControl)))
                {
                    control.IsActive = false;
                    ActiveControl = null;
                }

                // If this control or one of its children is the active control,
                // then handle mouse input on the active control.
                if (ActiveControl != null && control.IsActive)
                {
                    bool isInputCaptured = IsInputExclusivelyCaptured && SelectedControl != ActiveControl;

                    if (Cursor.LeftPressedDown)
                    {
                        if (!isInputCaptured)
                        {
                            ActiveControl.IsLeftPressedOn = true;
                            PropagateInputEvent(static (c, ie) => c.OnMouseLeftDown(ie), MouseInputFlags.LeftMouseButton);
                        }
                    }
                    else if (!Cursor.LeftDown && ActiveControl.IsLeftPressedOn)
                    {
                        ActiveControl.IsLeftPressedOn = false;
                        PropagateInputEvent(static (c, ie) => c.OnLeftClick(ie), MouseInputFlags.LeftMouseButton);
                    }

                    if (Cursor.RightPressedDown)
                    {
                        if (!isInputCaptured)
                        {
                            ActiveControl.IsRightPressedOn = true;
                            PropagateInputEvent(static (c, ie) => c.OnMouseRightDown(ie), MouseInputFlags.RightMouseButton);
                        }
                    }
                    else if (!Cursor.RightDown && ActiveControl.IsRightPressedOn)
                    {
                        ActiveControl.IsRightPressedOn = false;
                        PropagateInputEvent(static (c, ie) => c.OnRightClick(ie), MouseInputFlags.RightMouseButton);
                    }

                    if (Cursor.MiddlePressedDown)
                    {
                        if (!isInputCaptured)
                        {
                            ActiveControl.IsMiddlePressedOn = true;
                            PropagateInputEvent(static (c, ie) => c.OnMouseMiddleDown(ie), MouseInputFlags.MiddleMouseButton);
                        }
                    }
                    else if (!Cursor.MiddleDown && ActiveControl.IsMiddlePressedOn)
                    {
                        ActiveControl.IsMiddlePressedOn = false;
                        PropagateInputEvent(static (c, ie) => c.OnMiddleClick(ie), MouseInputFlags.MiddleMouseButton);
                    }

                    if (Cursor.ScrollWheelValue != 0 && !isInputCaptured)
                    {
                        PropagateInputEvent(static (c, ie) => c.OnMouseScrolled(ie), MouseInputFlags.ScrollWheel);
                    }
                    
                    if (Cursor.HorizontalScrollWheelValue != 0 && !isInputCaptured)
                    {
                        PropagateInputEvent(static (c, ie) => c.OnMouseScrolledHorizontally(ie), MouseInputFlags.ScrollWheelHorizontal);
                    }
                }
            }
        }

        // Make sure that, if input is exclusively captured:
        // 1) a mouse button is held down
        // 2) the control that is capturing the input is visible and enabled
        // If either of these conditions is not true, then release the exclusively captured input.
        if (SelectedControl != null && SelectedControl.ExclusiveInputCapture)
        {
            if ((!Cursor.RightDown && !Cursor.LeftDown) ||
                !SelectedControl.AppliesToSelfAndAllParents(p => p.Enabled && p.InputEnabled))
            {
                SelectedControl = null;
            }
        }
    }

    private void PropagateInputEvent(Action<XNAControl, InputEventArgs> action, MouseInputFlags mouseInputFlags)
    {
        var inputEventArgs = new InputEventArgs();
        XNAControl control = ActiveControl;

        while (control != null)
        {
            if (!control.InputPassthrough)
            {
                if ((control.HandledMouseInputs & mouseInputFlags) == mouseInputFlags)
                    inputEventArgs.Handled = true;

                action(control, inputEventArgs);
                if (inputEventArgs.Handled)
                    break;
            }

            control = control.Parent;
        }
    }

    /// <summary>
    /// Draws all the visible controls in the WindowManager.
    /// Do not call manually; MonoGame calls this automatically.
    /// </summary>
    /// <param name="gameTime">Provides a snapshot of timing values.</param>
    public override void Draw(GameTime gameTime)
    {
        GraphicsDevice.SetRenderTarget(renderTarget);

        GraphicsDevice.Clear(Color.Black);

        Renderer.ClearStack();
        Renderer.CurrentSettings = new SpriteBatchSettings(
            SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null);
        Renderer.BeginDraw();

        for (int i = 0; i < Controls.Count; i++)
        {
            var control = Controls[i];

            if (control.Visible)
                control.DrawInternal(gameTime);
        }

        Renderer.EndDraw();

        if (doubledRenderTarget != null)
        {
            GraphicsDevice.SetRenderTarget(doubledRenderTarget);
            GraphicsDevice.Clear(Color.Black);
            Renderer.CurrentSettings = new SpriteBatchSettings(SpriteSortMode.Deferred,
                BlendState.NonPremultiplied, SamplerState.PointWrap, null, null, null);
            Renderer.BeginDraw();
            Renderer.DrawTexture(renderTarget, new Rectangle(0, 0,
                RenderResolutionX * 2, RenderResolutionY * 2), Color.White);
            Renderer.EndDraw();
        }

        GraphicsDevice.SetRenderTarget(null);

        //if (Keyboard.PressedKeys.Contains(Microsoft.Xna.Framework.Input.Keys.F12))
        //{
        //    FileStream fs = File.Create(Environment.CurrentDirectory + "\\image.png");
        //    renderTarget.SaveAsPng(fs, renderTarget.Width, renderTarget.Height);
        //    fs.Close();
        //}

        GraphicsDevice.Clear(Color.Black);

        SamplerState scalingSamplerState = SamplerState.LinearClamp;
        if (ScaleRatio % 1.0 == 0)
            scalingSamplerState = SamplerState.PointClamp;

        Renderer.CurrentSettings = new SpriteBatchSettings(SpriteSortMode.Deferred,
                BlendState.NonPremultiplied, scalingSamplerState, null, null, null);
        Renderer.BeginDraw();

        RenderTarget2D renderTargetToDraw = doubledRenderTarget ?? renderTarget;

        Renderer.DrawTexture(renderTargetToDraw, new Rectangle(SceneXPosition, SceneYPosition,
            Game.Window.ClientBounds.Width - (SceneXPosition * 2), Game.Window.ClientBounds.Height - (SceneYPosition * 2)), Color.White);

#if DEBUG
        if (ShowDebugPanel)
            DrawDebugInfo();

        // Draw resize handles in editor mode
        if (EditorMode && ActiveControl != null && draggedControl == null)
            DrawResizeHandles(ActiveControl);
#endif

        if (Cursor.Visible)
            Cursor.Draw(gameTime);

        Renderer.EndDraw();

        base.Draw(gameTime);
    }

    private void DrawResizeHandles(XNAControl control)
    {
        const int HANDLE_SIZE = 6;
        Rectangle windowRect = control.GetWindowRectangle();

        Color handleColor = new Color(0, 255, 255, 200);

        //Corners
        // Top left
        Renderer.FillRectangle(new Rectangle(windowRect.Left - HANDLE_SIZE / 2, windowRect.Top - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);
        // Top right
        Renderer.FillRectangle(new Rectangle(windowRect.Right - HANDLE_SIZE / 2, windowRect.Top - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);
        // Bottom left
        Renderer.FillRectangle(new Rectangle(windowRect.Left - HANDLE_SIZE / 2, windowRect.Bottom - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);
        // Bottom right
        Renderer.FillRectangle(new Rectangle(windowRect.Right - HANDLE_SIZE / 2, windowRect.Bottom - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);

        //Middle of each edge
        // Top
        Renderer.FillRectangle(new Rectangle(windowRect.Left + windowRect.Width / 2 - HANDLE_SIZE / 2, windowRect.Top - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);
        // Bottom
        Renderer.FillRectangle(new Rectangle(windowRect.Left + windowRect.Width / 2 - HANDLE_SIZE / 2, windowRect.Bottom - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);
        // Left
        Renderer.FillRectangle(new Rectangle(windowRect.Left - HANDLE_SIZE / 2, windowRect.Top + windowRect.Height / 2 - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);
        // Right
        Renderer.FillRectangle(new Rectangle(windowRect.Right - HANDLE_SIZE / 2, windowRect.Top + windowRect.Height / 2 - HANDLE_SIZE / 2, HANDLE_SIZE, HANDLE_SIZE), handleColor);

        // Border around control
        Renderer.DrawRectangle(windowRect, new Color(0, 255, 255, 128), 1);
    }

    private Rectangle debugPanelBounds = Rectangle.Empty;

    private Rectangle GetDebugPanelBounds()
    {
        if (debugPanelBounds.IsEmpty)
            return new Rectangle(debugPanelPosition.X, debugPanelPosition.Y, 450, 300);
        return debugPanelBounds;
    }

    private TimeSpan iniCopiedNotificationTime = TimeSpan.Zero;

    private void HandleDebugPanelDragging()
    {
        Rectangle debugBounds = GetDebugPanelBounds();

        // Right-click to copy INI text
        if (EditorMode && Cursor.RightPressedDown && debugBounds.Contains(Cursor.Location) && ActiveControl != null)
        {
            CopyINIToClipboard();
            iniCopiedNotificationTime = TimeSpan.FromSeconds(2);
            return;
        }

        if (Cursor.LeftPressedDown && !isDraggingDebugPanel && debugBounds.Contains(Cursor.Location))
        {
            // Start dragging
            isDraggingDebugPanel = true;
            debugPanelDragOffset = new Point(Cursor.Location.X - debugPanelPosition.X,
                                            Cursor.Location.Y - debugPanelPosition.Y);
            return;
        }

        if (!Cursor.LeftDown && isDraggingDebugPanel)
        {
            // Stop dragging
            isDraggingDebugPanel = false;
            return;
        }

        if (isDraggingDebugPanel)
        {
            // Change pos
            debugPanelPosition = new Point(Cursor.Location.X - debugPanelDragOffset.X,
                                          Cursor.Location.Y - debugPanelDragOffset.Y);
            return;
        }
    }

    private void CopyINIToClipboard()
    {
        if (ActiveControl == null)
            return;

        var ctrl = ActiveControl;
        var iniText = new System.Text.StringBuilder();

        iniText.AppendLine($"[{ctrl.Name}]");
        iniText.AppendLine($"Location={ctrl.X},{ctrl.Y}");
        iniText.AppendLine($"Size={ctrl.Width},{ctrl.Height}");

        if (ctrl.Parent != null)
        {
            int distFromRight = ctrl.Parent.Width - ctrl.X - ctrl.Width;
            int distFromBottom = ctrl.Parent.Height - ctrl.Y - ctrl.Height;

            if (distFromRight >= 0)
                iniText.AppendLine($"DistanceFromRightBorder={distFromRight}");
            if (distFromBottom >= 0)
                iniText.AppendLine($"DistanceFromBottomBorder={distFromBottom}");
        }

#if WINFORMS
        try
        {
            System.Windows.Forms.Clipboard.SetText(iniText.ToString());
        }
        catch
        {
        }
#endif
    }

    private void HandleControlDragging()
    {
        const int EDGE_THRESHOLD = 8;

        if (isDraggingDebugPanel)
            return;

        if (Cursor.LeftPressedDown && ActiveControl != null && draggedControl == null)
        {
            draggedControl = ActiveControl;
            resizeStartCursorPos = Cursor.Location;
            originalControlBounds = draggedControl.ClientRectangle;

            Rectangle windowRect = draggedControl.GetWindowRectangle();
            Point cursorPos = Cursor.Location;

            bool nearLeft = cursorPos.X >= windowRect.Left && cursorPos.X <= windowRect.Left + EDGE_THRESHOLD;
            bool nearRight = cursorPos.X >= windowRect.Right - EDGE_THRESHOLD && cursorPos.X <= windowRect.Right;
            bool nearTop = cursorPos.Y >= windowRect.Top && cursorPos.Y <= windowRect.Top + EDGE_THRESHOLD;
            bool nearBottom = cursorPos.Y >= windowRect.Bottom - EDGE_THRESHOLD && cursorPos.Y <= windowRect.Bottom;

            if (nearLeft && nearTop)
                resizeMode = ResizeMode.ResizeTopLeft;
            else if (nearRight && nearTop)
                resizeMode = ResizeMode.ResizeTopRight;
            else if (nearLeft && nearBottom)
                resizeMode = ResizeMode.ResizeBottomLeft;
            else if (nearRight && nearBottom)
                resizeMode = ResizeMode.ResizeBottomRight;
            else if (nearLeft)
                resizeMode = ResizeMode.ResizeLeft;
            else if (nearRight)
                resizeMode = ResizeMode.ResizeRight;
            else if (nearTop)
                resizeMode = ResizeMode.ResizeTop;
            else if (nearBottom)
                resizeMode = ResizeMode.ResizeBottom;
            else
                resizeMode = ResizeMode.Move;

            if (resizeMode == ResizeMode.Move)
            {
                dragOffset = new Point(Cursor.Location.X - draggedControl.GetWindowPoint().X,
                                       Cursor.Location.Y - draggedControl.GetWindowPoint().Y);
            }
        }
        else if (!Cursor.LeftDown && draggedControl != null)
        {
            draggedControl = null;
            resizeMode = ResizeMode.None;
        }
        else if (draggedControl != null)
        {
            if (resizeMode == ResizeMode.Move)
            {
                Point newWindowPoint = new Point(Cursor.Location.X - dragOffset.X,
                                                Cursor.Location.Y - dragOffset.Y);

                if (draggedControl.Parent != null)
                {
                    Point parentWindowPoint = draggedControl.Parent.GetWindowPoint();
                    int parentTotalScaling = draggedControl.Parent.GetTotalScalingRecursive();
                    draggedControl.X = (newWindowPoint.X - parentWindowPoint.X) / parentTotalScaling;
                    draggedControl.Y = (newWindowPoint.Y - parentWindowPoint.Y) / parentTotalScaling;
                }
                else
                {
                    draggedControl.X = newWindowPoint.X;
                    draggedControl.Y = newWindowPoint.Y;
                }
            }
            else
            {
                // Resize the control
                int deltaX = Cursor.Location.X - resizeStartCursorPos.X;
                int deltaY = Cursor.Location.Y - resizeStartCursorPos.Y;

                // Apply scaling if control has a parent
                int parentTotalScaling = 1;
                if (draggedControl.Parent != null)
                {
                    parentTotalScaling = draggedControl.Parent.GetTotalScalingRecursive();
                    deltaX /= parentTotalScaling;
                    deltaY /= parentTotalScaling;
                }

                Rectangle newBounds = originalControlBounds;

                switch (resizeMode)
                {
                    case ResizeMode.ResizeLeft:
                        newBounds.X = originalControlBounds.X + deltaX;
                        newBounds.Width = Math.Max(10, originalControlBounds.Width - deltaX);
                        break;
                    case ResizeMode.ResizeRight:
                        newBounds.Width = Math.Max(10, originalControlBounds.Width + deltaX);
                        break;
                    case ResizeMode.ResizeTop:
                        newBounds.Y = originalControlBounds.Y + deltaY;
                        newBounds.Height = Math.Max(10, originalControlBounds.Height - deltaY);
                        break;
                    case ResizeMode.ResizeBottom:
                        newBounds.Height = Math.Max(10, originalControlBounds.Height + deltaY);
                        break;
                    case ResizeMode.ResizeTopLeft:
                        newBounds.X = originalControlBounds.X + deltaX;
                        newBounds.Y = originalControlBounds.Y + deltaY;
                        newBounds.Width = Math.Max(10, originalControlBounds.Width - deltaX);
                        newBounds.Height = Math.Max(10, originalControlBounds.Height - deltaY);
                        break;
                    case ResizeMode.ResizeTopRight:
                        newBounds.Y = originalControlBounds.Y + deltaY;
                        newBounds.Width = Math.Max(10, originalControlBounds.Width + deltaX);
                        newBounds.Height = Math.Max(10, originalControlBounds.Height - deltaY);
                        break;
                    case ResizeMode.ResizeBottomLeft:
                        newBounds.X = originalControlBounds.X + deltaX;
                        newBounds.Width = Math.Max(10, originalControlBounds.Width - deltaX);
                        newBounds.Height = Math.Max(10, originalControlBounds.Height + deltaY);
                        break;
                    case ResizeMode.ResizeBottomRight:
                        newBounds.Width = Math.Max(10, originalControlBounds.Width + deltaX);
                        newBounds.Height = Math.Max(10, originalControlBounds.Height + deltaY);
                        break;
                }

                draggedControl.ClientRectangle = newBounds;
            }
        }
    }

    private void DrawDebugInfo()
    {
        const int lineHeight = 16;
        const int padding = 8;
        const int panelWidth = 450;

        int currentY = 0;
        var lines = new List<(string text, Color color)>();

        lines.Add(EditorMode
            ? ("*** EDIT MODE (F11 to toggle) ***", Color.Yellow)
            : ("*** VIEW MODE (F11 to toggle) ***", Color.LightGray));

        if (EditorMode)
            lines.Add(("Right-click panel to copy INI", Color.Orange));

        lines.Add(("", Color.White));

        string controlName = ActiveControl?.Name ?? "none";
        lines.Add(($"Active control: {controlName}", Color.Red));

        if (ActiveControl != null)
        {
            var ctrl = ActiveControl;

            lines.Add(($"Position (relative): X={ctrl.X}, Y={ctrl.Y}", Color.White));
            var abs = ctrl.GetWindowPoint();
            lines.Add(($"Position (absolute): X={abs.X}, Y={abs.Y}", Color.White));
            lines.Add(($"Size: W={ctrl.Width}, H={ctrl.Height}", Color.White));

            var rect = ctrl.ClientRectangle;
            lines.Add(($"Bounds: X={rect.X}, Y={rect.Y}, R={rect.Right}, B={rect.Bottom}", Color.White));

            if (ctrl.Parent != null)
            {
                string hierarchy = BuildParentHierarchy(ctrl);
                lines.Add(($"Parent: {hierarchy}", Color.White));

                int distRight = ctrl.Parent.Width - ctrl.X - ctrl.Width;
                int distBottom = ctrl.Parent.Height - ctrl.Y - ctrl.Height;
                lines.Add(($"DistanceFromRight={distRight}, DistanceFromBottom={distBottom}", Color.White));
            }

            lines.Add(($"Enabled={ctrl.Enabled}, Visible={ctrl.Visible}", Color.White));

            if (EditorMode)
            {
                lines.Add(("", Color.White));
                lines.Add(("--- INI ---", Color.Lime));
                lines.Add(($"[{ctrl.Name}]", Color.Lime));
                lines.Add(($"Location={ctrl.X},{ctrl.Y}", Color.Lime));
                lines.Add(($"Size={ctrl.Width},{ctrl.Height}", Color.Lime));

                if (ctrl.Parent != null)
                {
                    int distRight = ctrl.Parent.Width - ctrl.X - ctrl.Width;
                    int distBottom = ctrl.Parent.Height - ctrl.Y - ctrl.Height;
                    if (distRight >= 0) lines.Add(($"DistanceFromRightBorder={distRight}", Color.Cyan));
                    if (distBottom >= 0) lines.Add(($"DistanceFromBottomBorder={distBottom}", Color.Cyan));
                }
            }
        }

        if (IMEHandler?.TextCompositionEnabled == true)
            lines.Add(("IME Enabled", Color.Red));

        if (iniCopiedNotificationTime > TimeSpan.Zero)
        {
            lines.Add(("", Color.White));
            lines.Add(("INI copied to clipboard", Color.Lime));
        }

        int panelHeight = lines.Count * lineHeight + padding * 2;

        var panelRect = new Rectangle(debugPanelPosition.X, debugPanelPosition.Y, panelWidth, panelHeight);
        debugPanelBounds = panelRect;

        Renderer.FillRectangle(panelRect, new Color(0, 0, 0, 200));
        Renderer.DrawRectangle(panelRect, isDraggingDebugPanel ? Color.Yellow : new Color(100, 100, 100, 255), 2);

        var titleBar = new Rectangle(debugPanelPosition.X, debugPanelPosition.Y, panelWidth, lineHeight + padding);
        Renderer.FillRectangle(titleBar, new Color(40, 40, 60, 220));

        currentY = debugPanelPosition.Y + padding;
        foreach (var (text, color) in lines)
        {
            Renderer.DrawString(text, 0, new Vector2(debugPanelPosition.X + padding, currentY), color, 1.0f);
            currentY += lineHeight;
        }
    }

    private string BuildParentHierarchy(XNAControl control)
    {
        var parts = new List<string>();
        var current = control.Parent;

        while (current != null)
        {
            parts.Add(current.Name ?? "(unnamed)");
            current = current.Parent;
        }

        return string.Join(" > ", parts);
    }
}