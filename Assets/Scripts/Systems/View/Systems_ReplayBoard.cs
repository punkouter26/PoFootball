using PoFootball.Models;
using UnityEngine;
using UnityEngine.UIElements;

namespace PoFootball.Views
{
    /// <summary>
    /// A highlight drawn as a broadcast telestrator board: the field turned on its
    /// side, the yard lines and the line of scrimmage, and every body as its own
    /// role shape, moving through the recorded play.
    ///
    /// WHY THE HIGHLIGHT IS NOT REPLAYED ON THE FIELD. It is the obvious place,
    /// and the instant replay does exactly that. But highlights belong to the
    /// final whistle, and at the final whistle Systems_HudView's final overlay
    /// lays a 92%-opaque scrim (Systems_UiTheme.SurfaceScrim) over the whole
    /// screen — ghosts on the turf underneath it are 8% visible. Making them
    /// visible means changing that overlay, which this view does not own. A board
    /// inside the reel's own card is visible over any scrim, needs no camera, and
    /// writes nothing outside its own element tree.
    ///
    /// TURNED ON ITS SIDE, NOT SHRUNK. The field is portrait and the free strip
    /// under the final card is landscape — about a thousand panel units by three
    /// hundred. Shrunk upright, a whole field fits at under three units a metre and
    /// a player is a two-pixel dot. Rotated a quarter turn clockwise (downfield
    /// is to the right, the offense always attacks +Y) and framed on the ball's
    /// path, a play lands at roughly fifteen to forty units a metre. It is a
    /// rotation, not a mirror, so the formation reads the right way round, and
    /// each shape is turned by the same quarter turn on top of its own heading.
    ///
    /// FRAMED ONCE PER HIGHLIGHT, NOT PER FRAME. The frame is the box around
    /// everywhere the ball went, padded and stretched to the board's aspect. A
    /// frame that followed the ball would scroll the yard lines under every body
    /// and the eye would lose the one fixed reference it has; a fixed frame means
    /// the only things moving on the board are the things that moved in the play.
    /// Bodies outside the frame are clipped — on a highlight, the far-side
    /// receiver who was never involved is not what anyone is looking at.
    ///
    /// SHAPES, NOT DOTS. Each token is the same sprite the player's renderer
    /// draws (Systems_RoleShapeApplier), as a UI Toolkit background image, so the
    /// "geometry encodes position" rule in CLAUDE.md survives onto the board. The
    /// colours are Systems_UiTheme's team colours — the same ones the box score
    /// card above it uses — and the carrier wears TextPrimary, as the real one
    /// wears white. The ball is drawn larger than life (see BALL_EMPHASIS): on a
    /// diagram it is the one thing every viewer is tracking.
    ///
    /// The ball's fake height cannot lift it up the screen here, because up the
    /// screen is now across the field. It grows instead, which reads as height
    /// without lying about where the ball is.
    ///
    /// Per frame it writes two style structs per token and allocates nothing.
    /// Layout properties (width, height, the lines) are written only when the
    /// frame changes; motion goes through translate and rotate, which do not
    /// dirty layout.
    /// </summary>
    internal sealed class Systems_ReplayBoard
    {
        /// <summary>Yard lines every five yards, goal line to goal line inclusive.</summary>
        private const int YARD_LINE_STEP_YARDS = 5;
        private const int YARD_LINE_COUNT = 21;

        /// <summary>Room left beyond the ball's path, downfield and upfield.</summary>
        private const float FRAME_MARGIN_LENGTH = 8f * Systems_FieldModel.YARD;

        /// <summary>Room left beyond the ball's path towards each sideline.</summary>
        private const float FRAME_MARGIN_WIDTH = 5f * Systems_FieldModel.YARD;

        /// <summary>
        /// The tightest a frame may be along the field. A two-yard dive framed on
        /// its own path would fill the board with four bodies at a hundred units a
        /// metre and show nothing of the line it was run at.
        /// </summary>
        private const float MIN_FRAME_LENGTH = 30f * Systems_FieldModel.YARD;

        /// <summary>How much larger than life the ball token is drawn. See the class note.</summary>
        private const float BALL_EMPHASIS = 1.75f;

        /// <summary>Token growth per metre of the ball's presentation height.</summary>
        private const float BALL_GROWTH_PER_METRE = 0.15f;

        private const float LINE_THICKNESS = 2f;
        private const float SCRIMMAGE_THICKNESS = 4f;

        /// <summary>The quarter turn clockwise that lays the field on its side.</summary>
        private const float VIEW_ROTATION_DEGREES = 90f;

        private static readonly Color YardLineColor = WithAlpha(Systems_UiTheme.TextMuted, 0.3f);
        private static readonly Color BoundaryColor = WithAlpha(Systems_UiTheme.TextPrimary, 0.55f);

        private readonly VisualElement _root;

        private readonly VisualElement[] _tokens;
        private readonly bool[] _tokenHasSprite;
        private readonly Vector2[] _worldSizes;
        private readonly Vector2[] _boardSizes;
        private readonly Systems_TeamSide[] _sides;
        private readonly int _count;

        private readonly VisualElement _ball;
        private readonly Vector2 _ballWorldSize;
        private readonly float _ballAngle;
        private Vector2 _ballBoardSize;

        private readonly VisualElement[] _yardLines = new VisualElement[YARD_LINE_COUNT];
        private readonly VisualElement _ownBackLine;
        private readonly VisualElement _attackingBackLine;
        private readonly VisualElement _nearSideline;
        private readonly VisualElement _farSideline;
        private readonly VisualElement _scrimmage;

        private Systems_ReplayHighlight _highlight;
        private bool _framed;
        private float _width;
        private float _height;
        private float _scale;
        private Vector2 _centre;
        private float _time;
        private int _paintedCarrier = Systems_ReplayTape.NO_CARRIER;

        public Systems_ReplayBoard(
            Sprite[] sprites,
            Vector2[] worldSizes,
            Systems_TeamSide[] sides,
            int count,
            Sprite ballSprite,
            Vector2 ballWorldSize,
            float ballAngle)
        {
            _count = count;
            _worldSizes = worldSizes;
            _sides = sides;
            _ballWorldSize = ballWorldSize * BALL_EMPHASIS;
            _ballAngle = ballAngle;

            _tokens = new VisualElement[count];
            _tokenHasSprite = new bool[count];
            _boardSizes = new Vector2[count];

            _root = new VisualElement { name = "ReplayBoard" };
            _root.style.overflow = Overflow.Hidden;
            _root.style.backgroundColor = Systems_UiTheme.SurfaceScrim;
            Systems_UiTheme.SetRadius(_root, Systems_UiTheme.RADIUS);
            _root.pickingMode = PickingMode.Ignore;

            // ORDER IS Z-ORDER: lines, then the offense, then the defense on top of
            // it as Systems_RoleShapeApplier sorts them on the field, then the ball.
            for (int line = 0; line < YARD_LINE_COUNT; line++)
            {
                bool goalLine = line == 0 || line == YARD_LINE_COUNT - 1;
                _yardLines[line] = Line(goalLine ? BoundaryColor : YardLineColor);
            }

            _ownBackLine = Line(BoundaryColor);
            _attackingBackLine = Line(BoundaryColor);
            _nearSideline = Line(BoundaryColor);
            _farSideline = Line(BoundaryColor);

            // The chains' colour, which Systems_UiTheme reserves for "the line".
            _scrimmage = Line(Systems_UiTheme.Accent);

            AddTokens(sprites, Systems_TeamSide.Offense);
            AddTokens(sprites, Systems_TeamSide.Defense);

            // Untinted: the ball sprite carries its own leather and laces. Without
            // a sprite it is a plain light token rather than nothing at all.
            _ball = Token(ballSprite, out bool ballHasSprite);
            _ball.style.unityBackgroundImageTintColor = Color.white;

            if (!ballHasSprite)
            {
                _ball.style.backgroundColor = Systems_UiTheme.TextPrimary;
            }

            _root.Add(_ball);

            _root.RegisterCallback<GeometryChangedEvent>(OnGeometryChanged);
        }

        public VisualElement Root => _root;

        /// <summary>Puts a highlight on the board and draws its first frame.</summary>
        public void Show(Systems_ReplayHighlight highlight)
        {
            _highlight = highlight;
            _paintedCarrier = Systems_ReplayTape.NO_CARRIER;

            PaintTeams();
            _framed = Reframe();
            Pose(0f);
        }

        /// <summary>Draws the play at <paramref name="time"/>, in recorded ticks.</summary>
        public void Pose(float time)
        {
            _time = time;

            if (!_framed || _highlight == null)
            {
                return;
            }

            Systems_ReplayTape tape = _highlight.Tape;
            int bodies = Mathf.Min(_count, tape.PlayerCount);

            for (int player = 0; player < bodies; player++)
            {
                tape.SamplePlayer(time, player, out Vector2 position, out float angle);

                Vector2 size = _boardSizes[player];
                VisualElement token = _tokens[player];

                token.style.translate = new Translate(
                    BoardX(position.y) - (size.x * 0.5f),
                    BoardY(position.x) - (size.y * 0.5f));

                token.style.rotate = new Rotate(
                    new Angle(VIEW_ROTATION_DEGREES - angle, AngleUnit.Degree));
            }

            int carrier = tape.CarrierAt(time);

            if (carrier != _paintedCarrier)
            {
                PaintPlayer(_paintedCarrier, false);
                PaintPlayer(carrier, true);
                _paintedCarrier = carrier;
            }

            tape.SampleBall(time, out Vector2 ballPosition, out float height);

            _ball.style.translate = new Translate(
                BoardX(ballPosition.y) - (_ballBoardSize.x * 0.5f),
                BoardY(ballPosition.x) - (_ballBoardSize.y * 0.5f));

            float growth = 1f + (Mathf.Max(0f, height) * BALL_GROWTH_PER_METRE);
            _ball.style.scale = new Scale(new Vector3(growth, growth, 1f));
        }

        /// <summary>
        /// The board's size is only known after layout, and changes with the
        /// screen — so framing is redone whenever it does, then the current frame
        /// is redrawn into the new geometry.
        /// </summary>
        private void OnGeometryChanged(GeometryChangedEvent evt)
        {
            if (_highlight == null)
            {
                return;
            }

            _framed = Reframe();
            Pose(_time);
        }

        /// <summary>
        /// Fits the frame to the highlight and lays out everything that depends on
        /// the scale. False until the board has been laid out at a real size.
        /// </summary>
        private bool Reframe()
        {
            if (_highlight == null)
            {
                return false;
            }

            _width = _root.resolvedStyle.width;
            _height = _root.resolvedStyle.height;

            if (float.IsNaN(_width) || float.IsNaN(_height) || _width <= 1f || _height <= 1f)
            {
                return false;
            }

            Systems_ReplayTape tape = _highlight.Tape;

            // The ball's path, plus the line it started from.
            float minX = float.MaxValue;
            float maxX = float.MinValue;
            float minY = _highlight.LineOfScrimmageY;
            float maxY = _highlight.LineOfScrimmageY;

            for (int frame = 0; frame < tape.Count; frame++)
            {
                Vector2 ball = tape.BallAt(frame);
                minX = Mathf.Min(minX, ball.x);
                maxX = Mathf.Max(maxX, ball.x);
                minY = Mathf.Min(minY, ball.y);
                maxY = Mathf.Max(maxY, ball.y);
            }

            if (minX > maxX)
            {
                minX = 0f;
                maxX = 0f;
            }

            float length = Mathf.Max(maxY - minY + (2f * FRAME_MARGIN_LENGTH), MIN_FRAME_LENGTH);
            float breadth = maxX - minX + (2f * FRAME_MARGIN_WIDTH);

            // Along the field is the board's width, across it the board's height.
            _scale = Mathf.Min(_width / length, _height / breadth);
            _centre = new Vector2((minX + maxX) * 0.5f, (minY + maxY) * 0.5f);

            for (int player = 0; player < _count; player++)
            {
                Vector2 size = _worldSizes[player] * _scale;
                _boardSizes[player] = size;
                _tokens[player].style.width = size.x;
                _tokens[player].style.height = size.y;
            }

            _ballBoardSize = _ballWorldSize * _scale;
            _ball.style.width = _ballBoardSize.x;
            _ball.style.height = _ballBoardSize.y;
            _ball.style.rotate = new Rotate(
                new Angle(VIEW_ROTATION_DEGREES - _ballAngle, AngleUnit.Degree));

            for (int line = 0; line < YARD_LINE_COUNT; line++)
            {
                float y = Systems_FieldModel.OWN_GOAL_LINE_Y
                    + (line * YARD_LINE_STEP_YARDS * Systems_FieldModel.YARD);

                PlaceAcross(_yardLines[line], y, LINE_THICKNESS);
            }

            PlaceAcross(_ownBackLine, Systems_FieldModel.OWN_BACK_LINE_Y, LINE_THICKNESS);
            PlaceAcross(_attackingBackLine, Systems_FieldModel.ATTACKING_BACK_LINE_Y, LINE_THICKNESS);
            PlaceAcross(_scrimmage, _highlight.LineOfScrimmageY, SCRIMMAGE_THICKNESS);

            PlaceAlong(_nearSideline, -Systems_FieldModel.HALF_WIDTH);
            PlaceAlong(_farSideline, Systems_FieldModel.HALF_WIDTH);

            return true;
        }

        /// <summary>Board x of a field Y: downfield is to the right.</summary>
        private float BoardX(float fieldY)
        {
            return (_width * 0.5f) + ((fieldY - _centre.y) * _scale);
        }

        /// <summary>Board y of a field X: the field's right-hand side is at the bottom.</summary>
        private float BoardY(float fieldX)
        {
            return (_height * 0.5f) + ((fieldX - _centre.x) * _scale);
        }

        /// <summary>A line across the field at <paramref name="fieldY"/> — vertical on the board.</summary>
        private void PlaceAcross(VisualElement line, float fieldY, float thickness)
        {
            line.style.left = BoardX(fieldY) - (thickness * 0.5f);
            line.style.top = BoardY(-Systems_FieldModel.HALF_WIDTH);
            line.style.width = thickness;
            line.style.height = Systems_FieldModel.FIELD_WIDTH * _scale;
        }

        /// <summary>A line along the field at <paramref name="fieldX"/> — horizontal on the board.</summary>
        private void PlaceAlong(VisualElement line, float fieldX)
        {
            line.style.left = BoardX(Systems_FieldModel.OWN_BACK_LINE_Y);
            line.style.top = BoardY(fieldX) - (LINE_THICKNESS * 0.5f);
            line.style.width = Systems_FieldModel.TOTAL_LENGTH * _scale;
            line.style.height = LINE_THICKNESS;
        }

        private void PaintTeams()
        {
            for (int player = 0; player < _count; player++)
            {
                PaintPlayer(player, false);
            }
        }

        private void PaintPlayer(int player, bool isCarrier)
        {
            if (player < 0 || player >= _count || _highlight == null)
            {
                return;
            }

            Systems_TeamId team = _sides[player] == Systems_TeamSide.Offense
                ? _highlight.Offense
                : _highlight.Offense.Opponent();

            Color color = isCarrier
                ? Systems_UiTheme.TextPrimary
                : Systems_UiTheme.ColorOf(team);

            VisualElement token = _tokens[player];

            if (_tokenHasSprite[player])
            {
                token.style.unityBackgroundImageTintColor = color;
            }
            else
            {
                token.style.backgroundColor = color;
            }
        }

        private void AddTokens(Sprite[] sprites, Systems_TeamSide side)
        {
            for (int player = 0; player < _count; player++)
            {
                if (_sides[player] != side)
                {
                    continue;
                }

                VisualElement token = Token(sprites[player], out bool hasSprite);
                _tokens[player] = token;
                _tokenHasSprite[player] = hasSprite;
                _root.Add(token);
            }
        }

        /// <summary>
        /// A positioned, non-pickable element drawing <paramref name="sprite"/>
        /// stretched over its box. A player whose shape never got assigned falls
        /// back to a rounded square, so a missing sprite is a plain token rather
        /// than an invisible one.
        /// </summary>
        private static VisualElement Token(Sprite sprite, out bool hasSprite)
        {
            VisualElement token = new VisualElement();
            token.pickingMode = PickingMode.Ignore;
            token.style.position = Position.Absolute;
            token.style.left = 0f;
            token.style.top = 0f;

            hasSprite = sprite != null;

            if (hasSprite)
            {
                token.style.backgroundImage = new StyleBackground(Background.FromSprite(sprite));
                token.style.backgroundSize = new BackgroundSize(
                    Length.Percent(100f), Length.Percent(100f));
            }
            else
            {
                Systems_UiTheme.SetRadius(token, Systems_UiTheme.RADIUS);
            }

            return token;
        }

        private VisualElement Line(Color color)
        {
            VisualElement line = new VisualElement();
            line.pickingMode = PickingMode.Ignore;
            line.style.position = Position.Absolute;
            line.style.backgroundColor = color;
            _root.Add(line);
            return line;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a = alpha;
            return color;
        }
    }
}
