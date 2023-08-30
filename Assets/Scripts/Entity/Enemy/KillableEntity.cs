﻿using UnityEngine;

using Fusion;
using NSMB.Entities.Enemies;
using NSMB.Entities.Player;
using NSMB.Extensions;
using NSMB.Game;
using NSMB.Tiles;
using NSMB.Utils;

namespace NSMB.Entities {
    public abstract class KillableEntity : FreezableEntity, IPlayerInteractable, IFireballInteractable {

        //---Static Variables
        private static readonly Enums.Sounds[] ComboSounds = {
            Enums.Sounds.Enemy_Shell_Kick,
            Enums.Sounds.Enemy_Shell_Combo1,
            Enums.Sounds.Enemy_Shell_Combo2,
            Enums.Sounds.Enemy_Shell_Combo3,
            Enums.Sounds.Enemy_Shell_Combo4,
            Enums.Sounds.Enemy_Shell_Combo5,
            Enums.Sounds.Enemy_Shell_Combo6,
            Enums.Sounds.Enemy_Shell_Combo7,
        };
        protected static readonly Collider2D[] CollisionBuffer = new Collider2D[32];
        protected static readonly ContactPoint2D[] ContactBuffer = new ContactPoint2D[32];
        protected static ContactFilter2D EntityFilter;

        //---Networked Variables
        [Networked(OnChanged = nameof(OnIsDeadChanged))] public NetworkBool IsDead { get; set; }
        [Networked] protected NetworkBool WasSpecialKilled { get; set; }
        [Networked] protected NetworkBool WasGroundpounded { get; set; }
        [Networked] protected float AngularVelocity { get; set; }
        [Networked] protected byte ComboCounter { get; set; }

        //---Properties
        public override bool IsCarryable => iceCarryable;
        public override bool IsFlying => flying;
        public override Vector2 FrozenSize {
            get {
                Bounds bounds = default;
                Renderer[] renderers = GetComponentsInChildren<Renderer>();
                foreach (Renderer renderer in renderers) {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer)
                        continue;

                    renderer.ResetBounds();

                    if (bounds == default)
                        bounds = new(renderer.bounds.center, renderer.bounds.size);
                    else
                        bounds.Encapsulate(renderer.bounds);
                }

                Vector2 size = bounds.size;
                return size;
            }
        }
        public override Vector2 FrozenOffset {
            get {

                Vector2 entityPosition = body ? body.position : transform.position;
                Bounds bounds = default;
                Renderer[] renderers = GetComponentsInChildren<Renderer>();
                foreach (Renderer renderer in renderers) {
                    if (!renderer.enabled || renderer is ParticleSystemRenderer)
                        continue;

                    renderer.ResetBounds();

                    if (bounds == default)
                        bounds = new(renderer.bounds.center, renderer.bounds.size);
                    else
                        bounds.Encapsulate(renderer.bounds);
                }

                Vector2 interpolationOffset = Vector2.zero;
                if (body && body.interpolationTarget) {
                    interpolationOffset = entityPosition - (Vector2) body.interpolationTarget.position;
                }

                Vector2 size = bounds.size;
                Vector2 position = new(bounds.center.x, bounds.min.y);
                Vector2 offset = entityPosition - position - interpolationOffset;

                return offset;
            }
        }

        //---Serialized Variables
        [SerializeField] protected bool iceCarryable = true;
        [SerializeField] protected bool flying = false;
        [SerializeField] public bool collideWithOtherEnemies = true;
        [SerializeField] protected bool dieWhenInsideBlock = true;
        [SerializeField] protected bool flipSpriteRenderer = false;

        //---Components
        [SerializeField] public BoxCollider2D hitbox;
        [SerializeField] protected Animator animator;
        [SerializeField] protected LegacyAnimateSpriteRenderer legacyAnimation;
        [SerializeField] public SpriteRenderer sRenderer;

        public override void OnValidate() {
            base.OnValidate();
            if (!hitbox) hitbox = GetComponent<BoxCollider2D>();
            if (!animator) animator = GetComponentInChildren<Animator>();
            if (!sRenderer) sRenderer = GetComponentInChildren<SpriteRenderer>();
            if (!legacyAnimation) legacyAnimation = GetComponentInChildren<LegacyAnimateSpriteRenderer>();
        }

        public virtual void Start() {
            if (!EntityFilter.useLayerMask)
                EntityFilter.SetLayerMask(Layers.MaskEntities);
        }

        public override void Spawned() {
            if (FirstSpawn) {
                SpawnLocation = transform.position;

                if (IsRespawningEntity)
                    DespawnEntity();
                else
                    RespawnEntity();
            }
            GameManager.Instance.networkObjects.Add(Object);
            OnFacingRightChanged();
            OnIsActiveChanged();

            FirstSpawn = false;
        }

        public override void Render() {
            base.Render();
            if (!IsActive || !body.interpolationTarget)
                return;

            if (IsDead)
                body.interpolationTarget.rotation *= Quaternion.Euler(0, 0, AngularVelocity * Time.deltaTime);
        }

        public override void FixedUpdateNetwork() {
            base.FixedUpdateNetwork();
            if (!GameData.Instance || !Object || !body)
                return;

            if (!IsActive) {
                gameObject.layer = Layers.LayerHitsNothing;
                AngularVelocity = 0;
                body.velocity = Vector2.zero;
                body.freeze = true;
                return;

            } else if (IsDead || IsFrozen) {
                gameObject.layer = Layers.LayerHitsNothing;
                body.freeze = false;

                if (WasSpecialKilled) {
                    AngularVelocity = 400f * (FacingRight ? 1 : -1);
                }
                return;
            } else {
                gameObject.layer = Layers.LayerEntity;
                body.freeze = false;
            }

            if (collideWithOtherEnemies) {
                CheckForEntityCollisions();
            }

            if (dieWhenInsideBlock) {
                Vector2 loc = body.position + hitbox.offset * transform.lossyScale;
                if (!body.freeze && Utils.Utils.IsTileSolidAtWorldLocation(loc)) {
                    SpecialKill(FacingRight, false, 0);
                }
            }
        }

        protected virtual void CheckForEntityCollisions() {

            int count = Runner.GetPhysicsScene2D().OverlapBox(body.position + hitbox.offset, hitbox.size, 0, CollisionBuffer, Layers.MaskEntities);

            for (int i = 0; i < count; i++) {
                GameObject obj = CollisionBuffer[i].gameObject;

                if (obj.transform.IsChildOf(transform))
                    continue;

                if (obj.GetComponent<KillableEntity>() is KillableEntity killable) {
                    if (killable.IsDead || !killable.collideWithOtherEnemies || killable is PiranhaPlant)
                        continue;

                    Utils.Utils.UnwrapLocations(body.position, killable.body.position, out Vector2 ourPos, out Vector2 theirPos);
                    bool goRight = ourPos.x > theirPos.x;

                    if (Mathf.Abs(ourPos.x - theirPos.x) < 0.015f) {
                        if (Mathf.Abs(ourPos.y - theirPos.y) < 0.015f) {
                            goRight = Object.Id.Raw < killable.Object.Id.Raw;
                        } else {
                            goRight = ourPos.y < theirPos.y;
                        }
                    }

                    FacingRight = goRight;
                }
            }
        }

        public virtual void Kill() {
            if (IsDead)
                return;

            SpecialKill(false, false, 0);
        }

        public virtual void SpecialKill(bool right, bool groundpound, int combo) {
            if (IsDead)
                return;

            IsDead = true;
            WasSpecialKilled = true;
            WasGroundpounded = groundpound;
            ComboCounter = (byte) combo;
            FacingRight = right;

            body.velocity = new(2f * (FacingRight ? 1 : -1), 2.5f);
            AngularVelocity = 400f * (FacingRight ? 1 : -1);
            body.gravity = Vector2.down * 14.75f;

            Runner.Spawn(PrefabList.Instance.Obj_LooseCoin, body.position + hitbox.offset);
        }

        public virtual void OnIsDeadChanged() {
            if (IsDead) {
                //death effects
                if (animator)
                    animator.enabled = false;
                sfx.enabled = true;

                if (WasSpecialKilled)
                    PlaySound(!IsFrozen ? ComboSounds[Mathf.Min(ComboSounds.Length - 1, ComboCounter)] : Enums.Sounds.Enemy_Generic_FreezeShatter);

                if (WasGroundpounded)
                    Instantiate(PrefabList.Instance.Particle_EnemySpecialKill, body.position + hitbox.offset, Quaternion.identity);

            } else {
                //undo death effects
                if (animator)
                    animator.enabled = true;
            }
        }

        public void PlaySound(Enums.Sounds sound) {
            sfx.PlayOneShot(sound);
        }

        //---BasicEntity overrides
        public override void OnIsActiveChanged() {
            if (IsActive) {
                if (sRenderer)
                    sRenderer.enabled = true;
            } else {
                if (sRenderer)
                    sRenderer.enabled = false;
            }
        }

        public override void OnFacingRightChanged() {
            sRenderer.flipX = FacingRight ^ flipSpriteRenderer;
        }

        public override void RespawnEntity() {
            if (IsActive)
                return;

            base.RespawnEntity();
            IsDead = false;
            IsFrozen = false;
            FacingRight = false;
            WasSpecialKilled = false;
            WasGroundpounded = false;
            ComboCounter = 0;

            if (body) {
                if (body.interpolationTarget)
                    body.interpolationTarget.rotation = Quaternion.identity;

                body.gravity = Vector2.down * 21.5f;
            }
        }

        public override void DespawnEntity(object data = null) {
            base.DespawnEntity(data);
            if (!Object)
                return;

            IsDead = true;
        }

        //---IPlayerInteractable overrides
        public virtual void InteractWithPlayer(PlayerController player) {

            Utils.Utils.UnwrapLocations(body.position + Vector2.up * 0.1f, player.body.position, out Vector2 ourPos, out Vector2 theirPos);
            Vector2 damageDirection = (theirPos - ourPos).normalized;
            bool attackedFromAbove = Vector2.Dot(damageDirection, Vector2.up) > 0.3f;

            bool groundpounded = attackedFromAbove && player.HasGroundpoundHitbox && player.State != Enums.PowerupState.MiniMushroom;
            if (player.InstakillsEnemies || groundpounded) {
                if (player.IsDrilling) {
                    Kill();
                    player.DoEntityBounce = true;
                } else {
                    SpecialKill(player.body.velocity.x > 0, player.IsGroundpounding, player.StarCombo++);
                }
                return;
            }

            if (attackedFromAbove) {
                if (player.State == Enums.PowerupState.MiniMushroom) {
                    if (player.IsGroundpounding) {
                        player.IsGroundpounding = false;
                        Kill();
                    }
                    player.DoEntityBounce = true;
                } else {
                    Kill();
                    player.DoEntityBounce = !player.IsGroundpounding;
                }

                player.IsDrilling = false;

            } else if (player.IsCrouchedInShell) {
                FacingRight = damageDirection.x < 0;
                player.body.velocity = new(0, player.body.velocity.y);

            } else if (player.IsDamageable) {
                player.Powerdown(false);
                FacingRight = damageDirection.x > 0;
            }
        }

        //---IFireballInteractable overrides
        public virtual bool InteractWithFireball(Fireball fireball) {
            if (IsDead)
                return false;

            SpecialKill(fireball.FacingRight, false, 0);
            return true;
        }

        public virtual bool InteractWithIceball(Fireball iceball) {
            if (IsDead)
                return false;

            if (!IsFrozen) {
                FrozenCube.FreezeEntity(Runner, this);
            }
            return true;
        }

        //---IBlockBumpable overrides
        public override void BlockBump(BasicEntity bumper, Vector2Int tile, InteractableTile.InteractionDirection direction) {
            SpecialKill(false, false, 0);
        }

        //---FreezableEntity overrides
        public override void Freeze(FrozenCube cube) {
            IsFrozen = true;

            if (body) {
                body.velocity = Vector2.zero;
                body.freeze = true;
            }
        }

        public override void Unfreeze(UnfreezeReason reasonByte) {
            IsFrozen = false;
            hitbox.enabled = true;

            SpecialKill(false, false, 0);
        }

        public override void OnIsFrozenChanged() {
            if (IsFrozen)
                sfx.Stop();

            if (animator)
                animator.enabled = !IsFrozen;
            if (legacyAnimation)
                legacyAnimation.enabled = !IsFrozen;
        }

        //---OnChangeds
        public static void OnIsDeadChanged(Changed<KillableEntity> changed) {
            changed.Behaviour.OnIsDeadChanged();
        }
    }
}
