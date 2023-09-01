﻿using Sandbox;
using System.ComponentModel;

namespace RunnerVision;

public partial class Pawn : AnimatedEntity
{
	[Net, Predicted]
	public Weapon ActiveWeapon { get; set; }

	[ClientInput]
	public Vector3 InputDirection { get; set; }

	[ClientInput]
	public Angles ViewAngles { get; set; }

	public float CameraTiltDeadzone => 10f;

	public float CameraTiltMax => 10f;

	public float CameraTiltMultiplier => 5f;

	public float CameraTilt { get; set; }
	public Rotation CameraNewRotation { get; set; }
	public bool CameraShouldRotateToNewPosition { get; set; }

	private Angles PreviousViewAngles { get; set; }

	public CameraPostProcessing PostProcessing { get; set; }

	[Net, Predicted]
	public AnimatedEntity CameraHelper { get; set; }

	private Rotation cameraStartRotation { get; set; }

	/// <summary>
	/// Position a player should be looking from in world space.
	/// </summary>
	[Browsable( false )]
	public Vector3 EyePosition
	{
		get => Transform.PointToWorld( EyeLocalPosition );
		set => EyeLocalPosition = Transform.PointToLocal( value );
	}

	/// <summary>
	/// Position a player should be looking from in local to the entity coordinates.
	/// </summary>
	[Net, Predicted, Browsable( false )]
	public Vector3 EyeLocalPosition { get; set; }

	/// <summary>
	/// Rotation of the entity's "eyes", i.e. rotation for the camera when this entity is used as the view entity.
	/// </summary>
	[Browsable( false )]
	public Rotation EyeRotation
	{
		get => Transform.RotationToWorld( EyeLocalRotation );
		set => EyeLocalRotation = Transform.RotationToLocal( value );
	}

	/// <summary>
	/// Rotation of the entity's "eyes", i.e. rotation for the camera when this entity is used as the view entity. In local to the entity coordinates.
	/// </summary>
	[Net, Predicted, Browsable( false )]
	public Rotation EyeLocalRotation { get; set; }

	public BBox Hull
	{
		get => new
		(
			new Vector3( -16, -16, 0 ),
			new Vector3( 16, 16, 64 )
		);
	}

	[BindComponent] public PawnController Controller { get; }
	[BindComponent] public PawnAnimator Animator { get; }

	public override Ray AimRay => new Ray( EyePosition, EyeRotation.Forward );

	public AnimatedEntity ShadowModel;

	bool IsThirdPerson { get; set; } = false;

	/// <summary>
	/// Called when the entity is first created 
	/// </summary>
	public override void Spawn()
	{
		SetModel( "models/faith_v2.vmdl" );

		EnableDrawing = true;
		EnableHideInFirstPerson = false;
		EnableShadowInFirstPerson = true;

		CameraHelper = new AnimatedEntity();
		CameraHelper.Position = Position + Model.GetBoneTransform( "CameraJoint" ).Position;
		CameraHelper.SetParent( this, "CameraJoint" );

		PostProcessing = Camera.Main.FindOrCreateHook<CameraPostProcessing>();

		EnableShadowCasting = false;

		ShadowModel = new( "models/faith_shadow.vmdl" );
		ShadowModel.SetParent( this, true );
		ShadowModel.EnableShadowOnly = true;
		ShadowModel.EnableShadowCasting = true;
	}

	public void UpdatePostProcessing()
	{
		if ( PostProcessing == null )
			return;

		PostProcessing.PawnMaxSpeed = Controller.CurrentMaxSpeed;
	}

	public void SetActiveWeapon( Weapon weapon )
	{
		ActiveWeapon?.OnHolster();
		ActiveWeapon = weapon;
		ActiveWeapon.OnEquip( this );
	}

	public void Respawn()
	{
		Components.Create<PawnController>();
		Components.Create<PawnAnimator>();

		// SetActiveWeapon( new Hands() );
	}

	public void DressFromClient( IClient cl )
	{
	}

	public override void Simulate( IClient cl )
	{
		UpdateAnimParameters();
		SimulateRotation();
		Controller?.Simulate( cl );
		Animator?.Simulate();
		ActiveWeapon?.Simulate( cl );
		EyeLocalPosition = Vector3.Up * (64f * Scale);

		UpdatePostProcessing();
	}

	void UpdateAnimParameters()
	{
		SetAnimParameter( "speed", Velocity.Length );
		SetAnimParameter( "jumping", !Controller.Grounded );
		SetAnimParameter( "dashing", Controller.Dashing );
		SetAnimParameter( "wallrunning", Controller.Wallrunning );
		SetAnimParameter( "vaulting", (int)Controller.Vaulting );
		SetAnimParameter( "climbing", Controller.Climbing );
	}

	public override void BuildInput()
	{
		InputDirection = Input.AnalogMove;

		if ( Input.StopProcessing )
			return;

		var look = Input.AnalogLook;

		if ( ViewAngles.pitch > 90f || ViewAngles.pitch < -90f )
		{
			look = look.WithYaw( look.yaw * -1f );
		}

		var viewAngles = ViewAngles;
		viewAngles += look;
		viewAngles.pitch = viewAngles.pitch.Clamp( -89f, 89f );
		viewAngles.roll = 0f;
		ViewAngles = viewAngles.Normal;
	}

	public override void FrameSimulate( IClient cl )
	{
		SimulateRotation();

		CameraUpdateRotation();
		CameraUpdateFOV();
		CameraUpdateTilt();

		if ( Input.Pressed( "view" ) )
		{
			ToggleThirdPerson();
		}

		if ( IsThirdPerson )
		{
			UpdateCameraThirdPerson();
		}
		else
		{
			UpdateCameraFirstPerson();
		}
	}

	private void ToggleThirdPerson()
	{
		IsThirdPerson = !IsThirdPerson;
	}

	private void UpdateCameraThirdPerson()
	{
		Vector3 targetPos;
		var pos = Position + Vector3.Up * 64;
		var rot = Camera.Rotation * Rotation.FromAxis( Vector3.Up, -16 );

		float distance = 80.0f * Scale;
		targetPos = pos + rot.Right * ((CollisionBounds.Mins.x + 50) * Scale);
		targetPos += rot.Forward * -distance;

		var tr = Trace.Ray( pos, targetPos )
			.WithAnyTags( "solid" )
			.Ignore( this )
			.Radius( 8 )
			.Run();

		Camera.FirstPersonViewer = null;
		Camera.Position = tr.EndPosition;
	}

	private void UpdateCameraFirstPerson()
	{
		bool turningLeft = ViewAngles.yaw.NormalizeDegrees() > PreviousViewAngles.yaw.NormalizeDegrees();
		float turnRate = PreviousViewAngles.ToRotation().Distance( ViewAngles.ToRotation() );

		if ( turnRate > CameraTiltDeadzone )
			CameraTilt = CameraTilt.LerpTo( turningLeft ? -CameraTiltMax : CameraTiltMax, Time.Delta * CameraTiltMultiplier );

		PreviousViewAngles = PreviousViewAngles.LerpTo( ViewAngles, Time.Delta * 50f );

		Camera.Rotation = Rotation.From( ViewAngles.pitch, ViewAngles.yaw, ViewAngles.roll + CameraTilt );
		Camera.FirstPersonViewer = this;

		Camera.Position = CameraHelper.Position + Rotation.Down * 3f + Rotation.Forward * 3f;
	}

	private void CameraUpdateTilt()
	{
		if ( Controller.Wallrunning != 0 )
		{
			CameraTilt = CameraTilt.LerpTo( Controller.Wallrunning == 1 ? 10f : -10f, Time.Delta * CameraTiltMultiplier );
			return;
		}

		if ( Controller.TimeSinceDash < 0.1f )
		{
			CameraTilt = CameraTilt.LerpTo( Controller.Dashing == 1 ? -10f : 10f, Time.Delta * CameraTiltMultiplier );
			return;
		}

		CameraTilt = CameraTilt.LerpTo( 0, Time.Delta * CameraTiltMultiplier * 1.1f );
	}

	private void CameraUpdateRotation()
	{
		if ( CameraShouldRotateToNewPosition )
			CameraRotateToNewPosition();
		else
			CameraRotateToViewAngles();
	}

	private void CameraRotateToViewAngles()
	{
		Camera.Rotation = ViewAngles.ToRotation();
	}

	private void CameraRotateToNewPosition()
	{
		var velocity = Vector3.One;
		Camera.Rotation = Camera.Rotation.SmoothDamp( cameraStartRotation, CameraNewRotation, ref velocity, 0.5f, Time.Delta );

		if ( Camera.Rotation.Distance( CameraNewRotation ) < 0.01f )
		{
			CameraShouldRotateToNewPosition = false;
		}
	}

	private void CameraUpdateFOV()
	{
		Camera.FieldOfView = Screen.CreateVerticalFieldOfView( Game.Preferences.FieldOfView );
	}

	public TraceResult TraceBBox( Vector3 start, Vector3 end, float liftFeet = 0.0f )
	{
		return TraceBBox( start, end, Hull.Mins, Hull.Maxs, liftFeet );
	}

	public TraceResult TraceBBox( Vector3 start, Vector3 end, Vector3 mins, Vector3 maxs, float liftFeet = 0.0f )
	{
		if ( liftFeet > 0 )
		{
			start += Vector3.Up * liftFeet;
			maxs = maxs.WithZ( maxs.z - liftFeet );
		}

		var tr = Trace.Ray( start, end )
					.Size( mins, maxs )
					.WithAnyTags( "solid", "playerclip", "passbullets" )
					.Ignore( this )
					.Run();

		return tr;
	}

	protected void SimulateRotation()
	{
		EyeRotation = ViewAngles.ToRotation();
		Rotation = ViewAngles.WithPitch( 0f ).ToRotation();
	}

	protected override void OnDestroy()
	{
		base.OnDestroy();

		ShadowModel?.Delete();
	}
}
