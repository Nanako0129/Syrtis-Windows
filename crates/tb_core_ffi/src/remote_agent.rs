//! The embedded remote-usage agent (`syrtis-agent`) on Windows: one `Agent`
//! per process, its management operations, and the copy of the app's pricing
//! files into the agent's pricing directory. Port of macOS `remote_agent.rs`
//! (Syrtis `f8a1a0ae`); the differences are listed in the SA-4c2 brief and
//! noted where they are.
//!
//! Nothing about pairing, grants, authorization, transport, caches or
//! uninstall is computed here. Management operations that exist as frozen
//! control commands (status, offer create and cancel, pending list, approve,
//! reject, revoke, listen configure) go through the agent's in-process entry
//! `Runtime::control` (Windows has no control socket, so no token is read and
//! no client is built). The only direct calls are
//! `Runtime::enable_sharing` / `disable_sharing` / `remove_peer`,
//! `Agent::approve_scope_for` with the caller's digest, the receiver's
//! `PairingClient`, `PeerStore` and `security::parse_pair_url` (the
//! destination preview), and one `set_listen_config` that writes the node
//! label back with the host and port the listen command just stored (the
//! frozen listen command carries no label).
//!
//! Errors are fixed class strings: never a path, a token, a Pair URL or an
//! address. The FFI entry points in `lib.rs` wrap one process-wide
//! [`RemoteAgent`]; tests build their own instances.

use std::{
    collections::{BTreeMap, HashMap},
    ffi::{c_char, OsString},
    fs,
    io::Write,
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, LazyLock, Mutex, MutexGuard, PoisonError, RwLock, RwLockReadGuard, RwLockWriteGuard,
    },
    thread,
    time::Duration,
};

use serde_json::{json, Value};
use sha2::{Digest, Sha256};
use syrtis_agent::{
    agent::{Agent, AgentConfig, AgentError, ScanRoots, ScannerSettings},
    cache_store::CachedBody,
    control::{self, Command, ControlError},
    noise::StaticKey,
    pairing::{ClaimAttempts, Claimed, ClientError, Clock, GrantStatus, PairingClient, PeerCode},
    peer_store::{PeerRecord, PeerState},
    report_peer::{FetchError, Fetched, ReportQuery},
    runtime::{Options, PullError, RuntimeError},
    secure_fs::StorageError,
    security::{
        clock_status, decode_fixed_hex, hex_encode, parse_pair_url, GrantState, MemoryLedger,
        SecurityError,
    },
    state_store::{StoredState, NODE_LABEL_MAX_BYTES},
};

pub(crate) type Reply = Result<Value, String>;

/// The engine's pricing files (`RemotePricingSnapshot::from_cache_root`).
pub(crate) const PRICING_FILES: [&str; 2] = ["pricing-litellm.json", "pricing-openrouter.json"];
/// The monitor period: how often the scan inputs are rebuilt (a changed
/// input restarts the agent) and a newer app pricing file is copied again.
/// Neither the app's pricing refresh nor a hand edit of the tokscale
/// `settings.json` has an FFI event to hook, so both are polled.
pub(crate) const PRICING_PERIOD: Duration = Duration::from_secs(300);
/// How long a (re)start waits for the previous owner lock to be released.
const BUSY_RETRIES: u32 = 100;
const BUSY_PAUSE: Duration = Duration::from_millis(20);

/// Everything an agent is started with: what `AgentConfig::with_scan_roots`
/// takes, plus the pricing copy source, the monitor period and the clock.
pub(crate) struct Inputs {
    /// The secure root's base (`secure_fs::trusted_home()` in the product).
    pub trusted_home: PathBuf,
    pub state_suffix: &'static [&'static str],
    /// The engine's four scan roots (`default_scan_roots()` in the product).
    pub roots: ScanRoots,
    pub settings: ScannerSettings,
    /// The app's pricing cache directory (the copy source).
    pub pricing_source: PathBuf,
    pub pricing_period: Duration,
    pub clock: Arc<dyn Clock>,
}

/// Builds the inputs from their sources, now. The same function serves the
/// start call and every monitor period, so both read the inputs the same
/// way. A restart builds them under the [`RemoteAgent`] write lock, so an
/// older snapshot cannot be applied after a newer one. It never touches
/// [`AGENT`].
pub(crate) type MakeInputs = Arc<dyn Fn() -> Result<Inputs, String> + Send + Sync>;

struct Running {
    agent: Agent,
    inputs: Inputs,
    /// Replaced by a start with unchanged inputs, which holds only the read
    /// lock.
    make: Mutex<MakeInputs>,
    /// Process-wide start counter; a restart gets a new one. Tests read it
    /// to tell a restart from a no-op.
    #[cfg(test)]
    epoch: u64,
}

#[derive(Default)]
struct Claims {
    next: u64,
    open: BTreeMap<u64, Claimed>,
    /// Kept for the life of the process: `PairingClient::claim` needs the
    /// attempts of earlier calls for the same offer.
    attempts: ClaimAttempts,
}

/// One embedded agent, the receiver's claims in progress, and the monitor
/// thread (started with the first agent, ended when this is dropped).
#[derive(Default)]
pub(crate) struct RemoteAgent {
    /// Read: every call that uses the running agent, including a producer
    /// pull for `remote_pages`, which holds it for the whole fetch. Write:
    /// only what replaces or removes the agent (a start or monitor period
    /// whose inputs changed, `stop`). A pull begun on one agent therefore
    /// ends before that agent is replaced, so it never writes into the next
    /// one; the cost is that a restart or stop waits for an in-flight fetch
    /// (at most `FETCH_DEADLINE`), and calls made while it waits queue behind
    /// it. No call takes a second read guard while it holds one.
    running: RwLock<Option<Running>>,
    /// Serializes `listen_configure`, which sends the frozen listen command,
    /// reads the stored address back and writes it again with the label.
    /// Under the shared `running` lock two such calls could interleave and
    /// leave the stored address different from the bound one.
    configuring: Mutex<()>,
    claims: Mutex<Claims>,
    monitor: AtomicBool,
    /// Unix seconds of the last fresh pull, by producer and query
    /// ([`last_success_key`]). This process only: after a restart of the
    /// process a cached body has no known age.
    last_success: Mutex<HashMap<String, i64>>,
    /// Test seam: the next monitor spawn fails as if the OS refused a thread.
    #[cfg(test)]
    fail_monitor_spawn: AtomicBool,
    /// Test seam: runs inside `listen_configure` after the stored address is
    /// read and before it is written back, the window the `configuring` lock
    /// closes.
    #[cfg(test)]
    listen_pause: Mutex<Option<Arc<dyn Fn() + Send + Sync>>>,
    /// Test seam: runs after every `Runtime::pull` in [`Self::pull_peer`]
    /// (counts the pulls; can revoke the grant between a code-2 refusal and
    /// its Grant status).
    #[cfg(test)]
    pub(crate) after_pull: Mutex<Option<Arc<dyn Fn() + Send + Sync>>>,
}

/// The process's agent, behind the `tb_remote_*` entry points.
pub(crate) static AGENT: LazyLock<Arc<RemoteAgent>> = LazyLock::new(Default::default);

/// An entry point body: the existing `guarded` boundary, with any panic
/// inside the agent answered by the fixed class `panic` (no panic text,
/// which `guarded` would put into `err` and the app would log).
pub(crate) fn ffi(name: &str, body: impl FnOnce() -> Reply) -> *mut c_char {
    crate::guarded(name, || {
        crate::envelope(
            std::panic::catch_unwind(std::panic::AssertUnwindSafe(body))
                .unwrap_or_else(|_| Err("panic".into())),
        )
    })
}

#[cfg(test)]
use std::sync::atomic::AtomicU64;
#[cfg(test)]
static EPOCH: AtomicU64 = AtomicU64::new(0);

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(PoisonError::into_inner)
}

fn read<T>(lock: &RwLock<T>) -> RwLockReadGuard<'_, T> {
    lock.read().unwrap_or_else(PoisonError::into_inner)
}

fn write<T>(lock: &RwLock<T>) -> RwLockWriteGuard<'_, T> {
    lock.write().unwrap_or_else(PoisonError::into_inner)
}

/// The running agent already serves these inputs: the same secure root, the
/// same four scan roots and equal scanner settings. A structural comparison
/// (macOS compares a digest it computes; here no digest is ever computed
/// outside the agent). Settings that do not serialize count as changed: an
/// extra restart only rescans.
fn unchanged(current: &Running, inputs: &Inputs) -> bool {
    let settings = |settings: &ScannerSettings| serde_json::to_value(settings).ok();
    current.inputs.trusted_home == inputs.trusted_home
        && current.inputs.state_suffix == inputs.state_suffix
        && current.inputs.roots == inputs.roots
        && settings(&current.inputs.settings)
            .is_some_and(|before| Some(before) == settings(&inputs.settings))
}

/// Makes `running` an agent over the inputs `make` builds now. Running with
/// unchanged inputs: kept (with `make` as its source from now on). Otherwise
/// the old agent is stopped (dropped) and one is started over the new inputs,
/// so a scan input the owner removed is no longer shared (the new inputs have
/// no approved scope until the owner approves them again). An error from
/// `make` leaves `running` as it was; an error after that leaves it empty.
fn ensure(running: &mut Option<Running>, make: MakeInputs) -> Result<(), String> {
    let inputs = make()?;
    if let Some(current) = running.as_mut() {
        if unchanged(current, &inputs) {
            *lock(&current.make) = make;
            return Ok(());
        }
    }
    // Drop (stop) the old agent before the new one takes the owner lock.
    *running = None;
    let agent = start_agent(&inputs)?;
    sync_pricing(&inputs.pricing_source, agent.pricing_dir());
    *running = Some(Running {
        agent,
        inputs,
        make: Mutex::new(make),
        #[cfg(test)]
        epoch: EPOCH.fetch_add(1, Ordering::Relaxed) + 1,
    });
    Ok(())
}

impl RemoteAgent {
    /// Starts the agent over the inputs `make` builds (see [`ensure`]), then
    /// the monitor if it does not run yet. Returns the status.
    pub(crate) fn start(self: &Arc<Self>, make: MakeInputs) -> Reply {
        // Unchanged inputs need no restart: answered under the read lock. A
        // running agent always has its monitor (a start that cannot spawn
        // one stops the agent it started). Inputs that cannot be built stop
        // the running agent, as a monitor period does (`recheck`): an input
        // or start error means no agent runs. A status read that fails
        // (`status_of`) is returned as is and leaves the agent running: it is
        // usually transient and changes no input or scope.
        let failed = {
            let running = read(&self.running);
            match running.as_ref() {
                Some(current) => match make() {
                    Ok(inputs) if unchanged(current, &inputs) => {
                        *lock(&current.make) = make;
                        return status_of(current);
                    }
                    Ok(_) => None,
                    Err(error) => Some(error),
                },
                None => None,
            }
        };
        if let Some(error) = failed {
            let old = write(&self.running).take();
            drop(old);
            return Err(error);
        }
        let mut running = write(&self.running);
        if let Err(error) = ensure(&mut running, make) {
            // `ensure` keeps the old agent when `make` fails; start does not.
            let old = running.take();
            drop(running);
            drop(old);
            return Err(error);
        }
        let current = running.as_ref().ok_or("internal")?;
        let period = current.inputs.pricing_period;
        let status = status_of(current);
        // Without the monitor a later input change would never be applied,
        // so an agent without one is not left running. The lock stays held
        // until the monitor has started or failed, so a failure stops the
        // agent this call started and not one a concurrent start put there.
        // The monitor sleeps a period before its first tick, so it does not
        // wait on this lock.
        if self.spawn_monitor(period).is_err() {
            *running = None;
            return Err("internal".into());
        }
        status
    }

    /// One thread per `RemoteAgent`, holding it weakly: each period it runs
    /// [`tick`](Self::tick); once the `RemoteAgent` is gone it ends. Already
    /// running: `Ok`. The thread could not be spawned: `Err`, and the next
    /// start tries again.
    fn spawn_monitor(self: &Arc<Self>, period: Duration) -> Result<(), ()> {
        if self.monitor.swap(true, Ordering::SeqCst) {
            return Ok(());
        }
        #[cfg(test)]
        if self.fail_monitor_spawn.swap(false, Ordering::SeqCst) {
            self.monitor.store(false, Ordering::SeqCst);
            return Err(());
        }
        let agent = Arc::downgrade(self);
        let spawned = thread::Builder::new()
            .name("tb-remote-monitor".into())
            .spawn(move || loop {
                thread::sleep(period);
                match agent.upgrade() {
                    // A panicking period (a `MakeInputs`, a restart) must not
                    // end the monitor: later input changes still have to be
                    // applied. Every lock here recovers from poisoning
                    // (`read`, `write`, `lock`), so the next period and
                    // every other caller go on.
                    Some(agent) => {
                        let _ =
                            std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| agent.tick()));
                    }
                    None => break,
                }
            });
        if spawned.is_err() {
            self.monitor.store(false, Ordering::SeqCst);
            return Err(());
        }
        Ok(())
    }

    /// One monitor period: the input re-check, then the pricing copy. Only a
    /// changed (or unreadable) input takes the write lock; an unchanged one
    /// is checked under the read lock.
    fn tick(&self) {
        let restart = {
            let running = read(&self.running);
            let Some(current) = running.as_ref() else {
                return;
            };
            let make = Arc::clone(&lock(&current.make));
            match make() {
                Ok(inputs) if unchanged(current, &inputs) => {
                    sync_pricing(&current.inputs.pricing_source, current.agent.pricing_dir());
                    false
                }
                _ => true,
            }
        };
        if restart {
            let mut running = write(&self.running);
            recheck(&mut running);
            if let Some(current) = running.as_ref() {
                sync_pricing(&current.inputs.pricing_source, current.agent.pricing_dir());
            }
        }
    }

    /// Stops the agent by dropping it (outside the write lock). Stopped
    /// already: the same answer.
    pub(crate) fn stop(&self) -> Reply {
        let old = write(&self.running).take();
        drop(old);
        Ok(json!({"running": false}))
    }

    /// Control `status` (its `scopeApproved` is the agent's own
    /// `Agent::scope_approved`, through the dispatcher's scope hook) plus
    /// `running`, `listening` and the identity's short fingerprint;
    /// `{"running":false}` when stopped.
    pub(crate) fn status(&self) -> Reply {
        match read(&self.running).as_ref() {
            Some(current) => status_of(current),
            None => Ok(json!({"running": false})),
        }
    }

    /// `Runtime::enable_sharing` / `disable_sharing` only. Disabling revokes
    /// every active grant and cancels offers and pending claims. Errors: the
    /// enable gate's `Busy` is `busy`; enabling while on is `invalid`;
    /// disabling with nothing to end is `ok`.
    pub(crate) fn sharing_set(&self, enabled: bool) -> Reply {
        self.with(|current| {
            let runtime = current.agent.runtime();
            let result = if enabled {
                runtime.enable_sharing()
            } else {
                runtime.disable_sharing()
            };
            result.map(|()| Value::Null).map_err(storage_class)
        })
    }

    /// The frozen `listenConfigure`, then the node label written with the
    /// host and port that command stored. Returns the status. The host is not
    /// parsed here: the frozen validator and the listener decide.
    ///
    /// The agent refuses a changed label that fails `valid_node_label` (after
    /// its own NFC step, which this crate cannot repeat: `unicode-normalization`
    /// is not a direct dependency). That refusal would come at the second step,
    /// after the address had already moved, so a changed label is first
    /// written alone, with the stored host and port: the agent's own check,
    /// before anything else changes. If the address step then fails, the old
    /// label is put back.
    pub(crate) fn listen_configure(&self, host: &str, port: u16, label: Option<String>) -> Reply {
        if label
            .as_ref()
            .is_some_and(|label| label.len() > NODE_LABEL_MAX_BYTES)
        {
            return Err("invalid".into());
        }
        let _one_at_a_time = lock(&self.configuring);
        self.with(|current| {
            let store = current.agent.runtime().store();
            let before = store.state().map_err(storage_class)?.config;
            let changed = label != before.node_label;
            if changed {
                store
                    .set_listen_config(before.bind_host.clone(), before.bind_port, label.clone())
                    .map_err(storage_class)?;
            }
            if let Err(error) = call(
                current,
                control::LISTEN_CONFIGURE,
                json!({"bindHost": host, "bindPort": port}),
            ) {
                if changed {
                    // Best effort: the old label may itself predate the rule.
                    let _ = store.set_listen_config(
                        before.bind_host,
                        before.bind_port,
                        before.node_label,
                    );
                }
                return Err(error);
            }
            let config = store.state().map_err(storage_class)?.config;
            #[cfg(test)]
            {
                let pause = lock(&self.listen_pause).clone();
                if let Some(pause) = pause {
                    pause();
                }
            }
            store
                .set_listen_config(config.bind_host, config.bind_port, label)
                .map_err(storage_class)?;
            status_of(current)
        })
    }

    /// What the running agent scans: `Agent::scope_disclosure` as the agent
    /// serializes it (the exact strings it hashed, camelCase, no path round
    /// trip), plus `ownerInputsSha256` (a field the struct does not
    /// serialize), the engine's client ids, and `envRoots`: the variables
    /// set in this process that the local pages follow and the shared source
    /// does not.
    pub(crate) fn scope_disclosure(&self) -> Reply {
        self.with(|current| {
            let disclosure = current.agent.scope_disclosure();
            let mut data = serde_json::to_value(disclosure).map_err(|_| "internal")?;
            let object = data.as_object_mut().ok_or("internal")?;
            object.insert(
                "ownerInputsSha256".into(),
                Value::String(disclosure.owner_inputs_sha256.clone()),
            );
            object.insert(
                "clients".into(),
                json!(tokscale_core::ClientId::iter()
                    .map(|c| c.as_str())
                    .collect::<Vec<_>>()),
            );
            let env_roots = env_roots();
            object.insert("envRootsSet".into(), Value::Bool(!env_roots.is_empty()));
            object.insert("envRoots".into(), Value::Array(env_roots));
            Ok(data)
        })
    }

    /// Owner consent to the disclosed scope: exactly
    /// `Agent::approve_scope_for(<the caller's digest>)`, which refuses
    /// (`invalid`, nothing written) unless it is the running agent's
    /// disclosed digest, so the consent binds to what the owner was shown.
    pub(crate) fn scope_approve(&self, digest: &str) -> Reply {
        self.with(|current| {
            current
                .agent
                .approve_scope_for(digest)
                .map(|()| Value::Null)
                .map_err(storage_class)
        })
    }

    /// The frozen `offerCreate`: `{expiresAt, offerId, pairUrl}`.
    pub(crate) fn offer_create(&self, ttl_seconds: i64) -> Reply {
        self.with(|current| {
            call(
                current,
                control::OFFER_CREATE,
                json!({"ttlSeconds": ttl_seconds}),
            )
        })
    }

    pub(crate) fn offer_cancel(&self, offer_id: &str) -> Reply {
        self.with(|current| call(current, control::OFFER_CANCEL, json!({"offerId": offer_id})))
    }

    /// The frozen `pendingList`: `[{expiresAt, pendingId, sasCode}]`.
    pub(crate) fn pending_list(&self) -> Reply {
        self.with(|current| call(current, control::PENDING_LIST, json!({})))
    }

    /// The frozen `approve` or `reject`.
    pub(crate) fn decide(&self, approve: bool, pending_id: &str, sas_generation: i64) -> Reply {
        let id = if approve {
            control::APPROVE
        } else {
            control::REJECT
        };
        self.with(|current| {
            call(
                current,
                id,
                json!({"pendingId": pending_id, "sasGeneration": sas_generation}),
            )
        })
    }

    /// The producer's grants, read from the state store.
    pub(crate) fn grants(&self) -> Reply {
        self.with(|current| {
            let state = current
                .agent
                .runtime()
                .store()
                .state()
                .map_err(storage_class)?;
            Ok(Value::Array(
                state
                    .reference
                    .grants
                    .iter()
                    .map(|grant| {
                        json!({
                            "grantId": hex_encode(&grant.grant_id),
                            "peerFingerprint": fingerprint(&grant.initiator_static),
                            "createdAt": grant.created_at_ms,
                            "expiresAt": grant.expires_at_ms,
                            // A stored `Active` grant stops authorizing when
                            // a scope approval moves the generation and
                            // raises the deny floor (`security::authorize`);
                            // the record itself is not rewritten. Listed as
                            // `active` it would show a device that can no
                            // longer read as one that can.
                            "state": match grant.state {
                                GrantState::Active
                                    if grant.auth_epoch <= state.reference.deny_floor
                                        || grant.source_scope_generation
                                            != state.reference.source_scope_generation =>
                                {
                                    "superseded"
                                }
                                GrantState::Active => "active",
                                GrantState::Revoked => "revoked",
                                GrantState::Expired => "expired",
                            },
                        })
                    })
                    .collect(),
            ))
        })
    }

    /// The frozen `revoke`.
    pub(crate) fn revoke(&self, grant_id: &str) -> Reply {
        self.with(|current| call(current, control::REVOKE, json!({"grantId": grant_id})))
    }

    /// Receiver: Pair claim. Returns a handle for [`pair_poll`] and the SAS
    /// to compare with the producer's. The URL is never kept or formatted.
    pub(crate) fn pair_claim(&self, pair_url: &str) -> Reply {
        let (identity, now_ms, clock) = self.with(|current| {
            let state = current
                .agent
                .runtime()
                .store()
                .state()
                .map_err(storage_class)?;
            let identity = StaticKey::from_state(&state).map_err(storage_class)?;
            let now_ms = effective_now_ms(current, &state)?;
            Ok((identity, now_ms, Arc::clone(&current.inputs.clock)))
        })?;
        let ledger = MemoryLedger::default();
        let client = PairingClient {
            identity: &identity,
            ledger: &ledger,
            clock: &*clock,
        };
        let mut claims = lock(&self.claims);
        let claimed = client
            .claim(pair_url, now_ms, &mut claims.attempts)
            .map_err(client_class)?;
        claims.next += 1;
        let handle = claims.next;
        let reply = json!({
            "handle": handle,
            "sas": claimed.sas,
            "expiresAt": claimed.expires_at_ms,
            "producerFingerprint": fingerprint(&claimed.responder_static),
        });
        claims.open.insert(handle, claimed);
        Ok(reply)
    }

    /// Receiver: where a Pair URL would connect, `{host, port}`, so the
    /// owner sees it before [`pair_claim`] connects. The same effective now
    /// and the same frozen `parse_pair_url` as the claim; no connection, and
    /// the URL and the offer (which holds the secret) are neither kept,
    /// formatted nor logged.
    pub(crate) fn pair_destination(&self, pair_url: &str) -> Reply {
        let now_ms = self.with(|current| {
            let state = current
                .agent
                .runtime()
                .store()
                .state()
                .map_err(storage_class)?;
            effective_now_ms(current, &state)
        })?;
        let offer = parse_pair_url(pair_url, now_ms).map_err(|error| match error {
            SecurityError::Expired => "expired",
            _ => "invalidOffer",
        })?;
        Ok(json!({"host": offer.host, "port": offer.port}))
    }

    /// Receiver: waits (blocking, up to the frozen poll window) for the
    /// producer's approval; on success the producer is written to
    /// `peers.json`. The handle ends with any answer.
    pub(crate) fn pair_poll(&self, handle: u64) -> Reply {
        let claimed = lock(&self.claims)
            .open
            .get(&handle)
            .cloned()
            .ok_or("invalid")?;
        let (identity, clock) = self.with(|current| {
            let state = current
                .agent
                .runtime()
                .store()
                .state()
                .map_err(storage_class)?;
            let identity = StaticKey::from_state(&state).map_err(storage_class)?;
            Ok((identity, Arc::clone(&current.inputs.clock)))
        })?;
        let ledger = MemoryLedger::default();
        let client = PairingClient {
            identity: &identity,
            ledger: &ledger,
            clock: &*clock,
        };
        // No lock is held while this waits.
        let result = client.poll(&claimed);
        lock(&self.claims).open.remove(&handle);
        let confirmed = result.map_err(client_class)?;
        self.with(|current| {
            // The agent may have been restarted on another identity while
            // this waited: the grant belongs to the identity that claimed it,
            // and goes into no other agent's peer store.
            let identity = current
                .agent
                .runtime()
                .store()
                .state()
                .and_then(|state| state.identity_public())
                .map_err(storage_class)?;
            if identity != confirmed.initiator_static {
                return Err("identityChanged".into());
            }
            let now_ms = current
                .inputs
                .clock
                .os_now_seconds()
                .map_err(storage_class)?
                .saturating_mul(1_000);
            current
                .agent
                .runtime()
                .peers()
                .upsert(PeerRecord::from_confirmed(&confirmed, None, now_ms))
                .map_err(storage_class)?;
            Ok(json!({
                "producerStatic": hex_encode(&confirmed.responder_static),
                "fingerprint": fingerprint(&confirmed.responder_static),
                "expiresAt": confirmed.expires_at_ms,
            }))
        })
    }

    /// Receiver: the paired producers.
    pub(crate) fn peers(&self) -> Reply {
        self.with(|current| {
            let peers = current
                .agent
                .runtime()
                .peers()
                .peers()
                .map_err(storage_class)?;
            Ok(Value::Array(
                peers
                    .iter()
                    .map(|peer| {
                        json!({
                            "producerStatic": hex_encode(&peer.responder_static),
                            "fingerprint": fingerprint(&peer.responder_static),
                            "nodeLabel": peer.node_label,
                            "host": peer.host,
                            "port": peer.port,
                            "createdAt": peer.created_at_ms,
                            "expiresAt": peer.expires_at_ms,
                            "state": match peer.state {
                                PeerState::Active => "active",
                                PeerState::Revoked => "revoked",
                                PeerState::Expired => "expired",
                            },
                        })
                    })
                    .collect(),
            ))
        })
    }

    /// Receiver: forgets one producer (`producerStatic`, 64 lowercase hex)
    /// and deletes its receiver cache entries (`Runtime::remove_peer`).
    pub(crate) fn peer_remove(&self, producer_static: &str) -> Reply {
        let key = decode_fixed_hex::<32>(producer_static).map_err(|_| "invalid")?;
        self.with(|current| {
            current
                .agent
                .runtime()
                .remove_peer(&key)
                .map(|()| Value::Null)
                .map_err(storage_class)
        })
    }

    fn with<T>(&self, body: impl FnOnce(&Running) -> Result<T, String>) -> Result<T, String> {
        match read(&self.running).as_ref() {
            Some(current) => body(current),
            None => Err("notRunning".into()),
        }
    }

    /// Test access to the running agent (its stores, identity, disclosure).
    #[cfg(test)]
    pub(crate) fn with_agent<T>(&self, body: impl FnOnce(&Agent) -> T) -> Option<T> {
        read(&self.running)
            .as_ref()
            .map(|current| body(&current.agent))
    }

    // ------------------------------------------- for `remote_pages` ---

    /// Receiver: every record of `peers.json`. `notRunning`, or the storage
    /// class when the file cannot be read.
    pub(crate) fn peer_records(&self) -> Result<Vec<PeerRecord>, String> {
        self.with(|current| {
            current
                .agent
                .runtime()
                .peers()
                .peers()
                .map_err(storage_class)
        })
    }

    /// `Runtime::pull` of one producer, under the agent's read lock for the
    /// whole fetch: other calls go on beside it, and a restart (write lock)
    /// waits until it ends, so it never writes into a newer agent. A fresh
    /// pull stamps [`Self::last_success_at`]. A refusal with error code 2
    /// (`CheckGrantStatus`) asks Grant status once and stores the answer
    /// ([`check_grant`]). Only when that answer was `active` and stored
    /// (`PeerStore::update_grant` succeeded) is the pull retried, once, with
    /// the refreshed record, under the same read lock; the retry's result is
    /// returned, a second code 2 included, as is. A code-2 refusal takes no
    /// token from the producer's per-grant limit (`spec/report-peer-v1.md`,
    /// "Limits"), so the retry is not refused as `busy` for it. `None`: no
    /// agent runs.
    pub(crate) fn pull_peer(
        &self,
        producer: &[u8; 32],
        query: &ReportQuery,
    ) -> Option<Result<Fetched, PullError>> {
        let running = read(&self.running);
        let current = running.as_ref()?;
        let runtime = current.agent.runtime();
        let pull = || {
            let result = runtime.pull(producer, query);
            #[cfg(test)]
            {
                let hook = lock(&self.after_pull).clone();
                if let Some(hook) = hook {
                    hook();
                }
            }
            result
        };
        let mut result = pull();
        if matches!(result, Err(PullError::Fetch(FetchError::CheckGrantStatus)))
            && check_grant(current, producer)
        {
            result = pull();
        }
        if result.is_ok() {
            if let Ok(now) = current.inputs.clock.os_now_seconds() {
                lock(&self.last_success).insert(last_success_key(producer, query), now);
            }
        }
        Some(result)
    }

    /// `Runtime::last_good` of one producer; `None` for no agent, no body or
    /// a storage error.
    pub(crate) fn last_good_peer(
        &self,
        producer: &[u8; 32],
        query: &ReportQuery,
    ) -> Option<CachedBody> {
        let running = read(&self.running);
        let current = running.as_ref()?;
        current
            .agent
            .runtime()
            .last_good(producer, query)
            .ok()
            .flatten()
    }

    /// `Runtime::ever_answered`: the producer ever gave this receiver a body
    /// (`false` too when no agent runs).
    pub(crate) fn ever_answered(&self, producer: &[u8; 32]) -> bool {
        read(&self.running)
            .as_ref()
            .is_some_and(|current| current.agent.runtime().ever_answered(producer))
    }

    /// The Unix seconds of the last fresh [`Self::pull_peer`] of this query
    /// from this producer in this process.
    pub(crate) fn last_success_at(&self, producer: &[u8; 32], query: &ReportQuery) -> Option<i64> {
        lock(&self.last_success)
            .get(&last_success_key(producer, query))
            .copied()
    }
}

fn last_success_key(producer: &[u8; 32], query: &ReportQuery) -> String {
    format!(
        "{}\0{}\0{}\0{}\0{}\0{}",
        hex_encode(producer),
        u16::from(query.endpoint),
        query.start_date,
        query.end_date_exclusive,
        query.timezone,
        query.clients.join("\0")
    )
}

/// One Grant status request for the producer's record, built from that
/// record (host, port, responder static), and the answer stored. Any failure
/// changes nothing. True only when the answer was `active` and `update_grant`
/// stored it: the record now carries the grant's current `authEpoch`, so a
/// pull is worth retrying.
fn check_grant(current: &Running, producer: &[u8; 32]) -> bool {
    let runtime = current.agent.runtime();
    let Some(record) = runtime.peers().peers().ok().and_then(|peers| {
        peers
            .into_iter()
            .find(|peer| peer.responder_static == *producer)
    }) else {
        return false;
    };
    let Ok(identity) = runtime
        .store()
        .state()
        .and_then(|state| StaticKey::from_state(&state))
    else {
        return false;
    };
    let ledger = MemoryLedger::default();
    let client = PairingClient {
        identity: &identity,
        ledger: &ledger,
        clock: &*current.inputs.clock,
    };
    // Grant status reads only the host, port and responder static.
    let claimed = Claimed {
        offer_id: [0; 16],
        responder_static: record.responder_static,
        host: record.host.clone(),
        port: record.port,
        pending_id: record.pending_id,
        sas: String::new(),
        expires_at_ms: record.expires_at_ms,
        claimed_at: Duration::ZERO,
    };
    let Ok(answer) = client.grant_status(&claimed, record.grant_id) else {
        return false;
    };
    let peers = runtime.peers();
    match answer.state {
        GrantStatus::Revoked => {
            let _ = peers.set_state(producer, &record.grant_id, PeerState::Revoked);
            false
        }
        GrantStatus::Expired => {
            let _ = peers.set_state(producer, &record.grant_id, PeerState::Expired);
            false
        }
        GrantStatus::Active => peers
            .update_grant(
                producer,
                &record.grant_id,
                answer.auth_epoch,
                answer.source_scope_generation,
                answer.expires_at_ms,
            )
            .is_ok(),
    }
}

fn start_agent(inputs: &Inputs) -> Result<Agent, String> {
    let mut config = AgentConfig::with_scan_roots(
        inputs.trusted_home.clone(),
        inputs.roots.clone(),
        inputs.settings.clone(),
    );
    config.state_suffix = inputs.state_suffix;
    config.options = Options {
        clock: Arc::clone(&inputs.clock),
        ..Options::default()
    };
    let mut tries = 0;
    loop {
        match Agent::start(config.clone()) {
            Ok(agent) => return Ok(agent),
            // A just-dropped agent can hold the owner lock for a moment.
            Err(AgentError::Storage(StorageError::Busy))
            | Err(AgentError::Runtime(RuntimeError::Store(StorageError::Busy)))
                if tries < BUSY_RETRIES =>
            {
                tries += 1;
                thread::sleep(BUSY_PAUSE);
            }
            Err(error) => return Err(agent_class(error)),
        }
    }
}

fn status_of(current: &Running) -> Reply {
    let mut data = call(current, control::STATUS, json!({}))?;
    let runtime = current.agent.runtime();
    let identity = runtime
        .store()
        .state()
        .and_then(|state| state.identity_public())
        .map_err(storage_class)?;
    let object = data.as_object_mut().ok_or("internal")?;
    object.insert("running".into(), Value::Bool(true));
    object.insert(
        "listening".into(),
        Value::Bool(runtime.listener_addr().is_some()),
    );
    object.insert(
        "identityFingerprint".into(),
        Value::String(fingerprint(&identity)),
    );
    Ok(data)
}

/// One frozen control command through the agent's in-process entry
/// (`Runtime::control`, `spec/control-v1.md` "In-process entry"). Blocking,
/// no deadline. Windows has no control socket: no token is read.
fn call(current: &Running, id: u16, body: Value) -> Reply {
    let response = current
        .agent
        .runtime()
        .control(&Command { id, body })
        .map_err(control_class)?;
    if response.code == control::OK {
        Ok(response.data)
    } else {
        Err(code_class(response.code).into())
    }
}

/// First 8 bytes of SHA-256 of a static key, lowercase hex: for people to
/// compare, not an identifier.
fn fingerprint(key: &[u8; 32]) -> String {
    hex_encode(&Sha256::digest(key)[..8])
}

/// The root variables of this process that make the local pages and the
/// shared source see different directories ([`env_roots_from`]).
fn env_roots() -> Vec<Value> {
    env_roots_from(
        &|var| std::env::var_os(var),
        dirs::config_dir(),
        dirs::data_local_dir(),
    )
}

/// `{client, variable}` for each root variable the local report follows and
/// sharing does not, as `get` reads them: `HOME` (`client` null) when set
/// and non-empty; `APPDATA` / `LOCALAPPDATA` (`client` null) only when set,
/// non-empty and different from the RoamingAppData / LocalAppData known
/// folder (`config_dir` / `data_local_dir`; an unavailable known folder
/// counts as different); then each client's own root variables when set and
/// non-empty. The local report reads `APPDATA` and `LOCALAPPDATA` from the
/// process environment for some clients (tokscale-core `source_context.rs`
/// `resolve_source_environment_paths`) while sharing uses the known folders
/// only, so only a value that differs changes what the two see; both are
/// set in every Windows session, so listing them whenever set would always
/// report a difference.
fn env_roots_from(
    get: &dyn Fn(&str) -> Option<OsString>,
    config_dir: Option<PathBuf>,
    data_local_dir: Option<PathBuf>,
) -> Vec<Value> {
    use tokscale_core::PathRoot;
    let value = |var: &str| get(var).filter(|value| !value.is_empty());
    let differs = |var: &str, known: &Option<PathBuf>| {
        value(var).is_some_and(|value| known.as_ref() != Some(&PathBuf::from(value)))
    };
    let mut roots = Vec::new();
    for (var, listed) in [
        ("HOME", value("HOME").is_some()),
        ("APPDATA", differs("APPDATA", &config_dir)),
        ("LOCALAPPDATA", differs("LOCALAPPDATA", &data_local_dir)),
    ] {
        if listed {
            roots.push(json!({"client": null, "variable": var}));
        }
    }
    for client in tokscale_core::ClientId::iter() {
        let variables: Vec<&'static str> = match client.data().root {
            PathRoot::EnvVar { var, .. } => vec![var],
            PathRoot::ReasonixHome => vec!["REASONIX_STATE_HOME", "REASONIX_HOME"],
            PathRoot::XdgData => vec!["XDG_DATA_HOME"],
            PathRoot::Config => vec!["TOKSCALE_CONFIG_DIR"],
            PathRoot::Home => vec![],
        };
        for var in variables {
            if value(var).is_some() {
                roots.push(json!({"client": client.as_str(), "variable": var}));
            }
        }
    }
    roots
}

/// The receiver's effective now in milliseconds: the OS clock, never below
/// the last accepted wall time in `state` (`clock_status`). Pair claim and
/// the destination preview read it the same way.
fn effective_now_ms(current: &Running, state: &StoredState) -> Result<i64, String> {
    let os_now = current
        .inputs
        .clock
        .os_now_seconds()
        .map_err(storage_class)?;
    let now = clock_status(os_now, state.reference.last_accepted_wall_unix_seconds());
    now.effective_now
        .checked_mul(1_000)
        .ok_or_else(|| "invalid".into())
}

fn storage_class(error: StorageError) -> String {
    match error {
        StorageError::Refused(SecurityError::Unauthorized) => "unauthorized",
        StorageError::Refused(SecurityError::Expired) => "expired",
        StorageError::Refused(_) => "invalid",
        StorageError::Busy => "busy",
        StorageError::UnsafeState => "unsafeState",
        _ => "internal",
    }
    .into()
}

fn agent_class(error: AgentError) -> String {
    match error {
        AgentError::Unsupported => "unsupported".into(),
        AgentError::Storage(error) | AgentError::Runtime(RuntimeError::Store(error)) => {
            storage_class(error)
        }
        AgentError::Runtime(RuntimeError::Control(error)) => control_class(error),
        AgentError::Runtime(RuntimeError::Thread) | AgentError::Install(_) => "internal".into(),
    }
}

/// `Stopped` (the in-process entry after the runtime stopped) is
/// `notRunning`; `Unsupported` is `unsupported`; everything else is
/// `control`: `Command`, `Unavailable`, `Response`, and the socket-path
/// variants (`PathTooLong`, `UnsafeSocket`, `Socket`, `NoReply`,
/// `ServerProof`), which the in-process entry never returns on Windows (no
/// socket) and which get no class of their own here.
fn control_class(error: ControlError) -> String {
    match error {
        ControlError::Stopped => "notRunning",
        ControlError::Unsupported => "unsupported",
        _ => "control",
    }
    .into()
}

fn code_class(code: u16) -> &'static str {
    match code {
        control::INVALID => "invalid",
        control::UNAUTHORIZED => "unauthorized",
        control::EXPIRED => "expired",
        control::BUSY => "busy",
        control::UNSAFE_STATE => "unsafeState",
        _ => "internal",
    }
}

fn client_class(error: ClientError) -> String {
    match error {
        ClientError::SelfPairing => "selfPairing",
        ClientError::InvalidOffer => "invalidOffer",
        ClientError::OfferUnusable => "offerUnusable",
        ClientError::LostAttempt => "lostAttempt",
        ClientError::Code(PeerCode::Busy) => "busy",
        ClientError::Code(PeerCode::Invalid) => "invalid",
        ClientError::Code(PeerCode::Unauthorized) => "unauthorized",
        ClientError::Code(PeerCode::Incompatible) => "incompatible",
        ClientError::Code(PeerCode::Internal) => "internal",
        ClientError::Rejected => "rejected",
        ClientError::Expired => "expired",
        ClientError::Revoked => "revoked",
        ClientError::Aborted => "aborted",
        ClientError::PollLimit => "pollLimit",
        ClientError::Transport(_) => "transport",
    }
    .into()
}

/// The monitor's re-check: the running agent's own `make` again, exactly as
/// a start call. Unchanged: nothing happens. Changed: restarted over the new
/// inputs. Inputs that cannot be built any more (the known folders failed):
/// stopped, so a scope that can no longer be read is not served.
fn recheck(running: &mut Option<Running>) {
    let Some(make) = running
        .as_ref()
        .map(|current| Arc::clone(&lock(&current.make)))
    else {
        return;
    };
    if ensure(running, make).is_err() {
        *running = None;
    }
}

// ------------------------------------------------------------- pricing ---

/// Copies each pricing file whose source is newer than its copy (or has no
/// copy yet). A missing source is skipped; a failed copy is retried next
/// period.
fn sync_pricing(source: &Path, destination: &Path) {
    for name in PRICING_FILES {
        let Ok(modified) = fs::metadata(source.join(name)).and_then(|m| m.modified()) else {
            continue;
        };
        let stale = fs::metadata(destination.join(name))
            .and_then(|m| m.modified())
            .ok()
            .is_none_or(|copied| modified > copied);
        if stale {
            let _ = copy_atomic(&source.join(name), destination, name);
        }
    }
}

/// A temp file created exclusively (`create_new`) in `directory` (the
/// agent's pricing directory, whose inheritable owner-only ACE the new file
/// takes), written, flushed, then renamed over `name` in the same directory.
/// Nothing else in the agent root is written.
fn copy_atomic(source: &Path, directory: &Path, name: &str) -> std::io::Result<()> {
    let bytes = fs::read(source)?;
    let temp = directory.join(format!(".{name}.tmp"));
    let _ = fs::remove_file(&temp);
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temp)?;
    file.write_all(&bytes)?;
    file.sync_all()?;
    drop(file);
    fs::rename(&temp, directory.join(name))
}

// ---------------------------------------------------------- app inputs ---

/// The app's [`MakeInputs`] for `tb_remote_start`: the product roots, the
/// process scanner settings read fresh on every call
/// ([`crate::process_scanner_settings`], never the cached context), the
/// engine's pricing cache and the system clock. A test build gets
/// `homeRequired` before anything is read.
pub(crate) fn app_make_inputs() -> MakeInputs {
    Arc::new(|| {
        let (trusted_home, roots) = product_roots()?;
        Ok(Inputs {
            trusted_home,
            state_suffix: syrtis_agent::secure_fs::STATE_SUFFIX,
            roots,
            settings: crate::process_scanner_settings(),
            pricing_source: tokscale_core::pricing::cache::get_cache_dir(),
            pricing_period: PRICING_PERIOD,
            clock: Arc::new(syrtis_agent::pairing::SystemClock),
        })
    })
}

/// The secure root's base and the four scan roots, from the process token's
/// known folders (no environment variable).
#[cfg(not(test))]
fn product_roots() -> Result<(PathBuf, ScanRoots), String> {
    let home = syrtis_agent::secure_fs::trusted_home().map_err(storage_class)?;
    let roots = syrtis_agent::agent::default_scan_roots().map_err(storage_class)?;
    Ok((home, roots))
}

/// A test build never opens the real agent root or reads the real scan
/// roots: every test injects its [`Inputs`].
#[cfg(test)]
fn product_roots() -> Result<(PathBuf, ScanRoots), String> {
    Err("homeRequired".into())
}

#[cfg(test)]
pub(crate) mod tests {
    //! Every agent here lives in a temp home below the temp directory (its
    //! long, non-verbatim form, as the agent's own `tests/support` makes
    //! one) that the test creates, with a protected owner-only DACL
    //! ([`create_owner_only_dir`]), and removes by its exact path. Every input
    //! is injected; nothing reads the product roots (`app_make_inputs` is
    //! `homeRequired` in a test build). Every listener binds `127.0.0.1`.

    use super::*;
    use std::{
        ffi::{CStr, CString},
        net::{TcpListener, TcpStream},
        sync::atomic::{AtomicI64, AtomicUsize},
        time::{Instant, SystemTime, UNIX_EPOCH},
    };
    use syrtis_agent::{
        report::Endpoint,
        report_peer::{FetchError, ReportQuery},
        runtime::PullError,
    };

    /// A short secure-root suffix below each temp home (not pre-created).
    const TEST_SUFFIX: &[&str] = &["a"];

    /// Injected time from the real wall clock; `sleep` advances it, so the
    /// receiver's poll and the grant's rate tokens do not wait.
    pub(crate) struct TestClock {
        os: AtomicI64,
        mono: Mutex<Duration>,
    }

    impl TestClock {
        pub(crate) fn new() -> Arc<Self> {
            let now = SystemTime::now()
                .duration_since(UNIX_EPOCH)
                .unwrap()
                .as_secs();
            Arc::new(Self {
                os: AtomicI64::new(now as i64),
                mono: Mutex::new(Duration::ZERO),
            })
        }

        pub(crate) fn advance(&self, seconds: u64) {
            *lock(&self.mono) += Duration::from_secs(seconds);
            self.os.fetch_add(seconds as i64, Ordering::SeqCst);
        }
    }

    impl Clock for TestClock {
        fn monotonic(&self) -> Duration {
            *lock(&self.mono)
        }
        fn os_now_seconds(&self) -> Result<i64, StorageError> {
            Ok(self.os.load(Ordering::SeqCst))
        }
        fn sleep(&self, duration: Duration) {
            self.advance(duration.as_secs().max(1));
        }
    }

    /// A fresh temp home `<temp>\sa4c2-<pid>-<n>`; only it is removed.
    pub(crate) struct TempHome(pub(crate) PathBuf);

    impl TempHome {
        pub(crate) fn new() -> Self {
            static NEXT: AtomicUsize = AtomicUsize::new(0);
            let path = temp_base().join(format!(
                "sa4c2-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            // Exclusive: an existing path fails the test instead of being
            // written through.
            create_owner_only_dir(&path);
            Self(path)
        }
    }

    /// Creates `path` (exclusively) with an explicit protected DACL: owner
    /// and one inheritable full-control ACE for this process's user, no
    /// inherited ACE. Written for agent d3fc9177, whose base rule refused a
    /// home that let another SID write (188's profile carries
    /// `CodexSandboxUsers` Modify); since f5e09711 (W4) the base may be
    /// writable by others, so this is no longer required, only harmless. The
    /// agent's own suffix directory below it is not created here;
    /// `open_under` creates it.
    fn create_owner_only_dir(path: &Path) {
        use std::os::windows::ffi::OsStrExt;
        use windows_sys::Win32::{
            Foundation::{CloseHandle, LocalFree, HANDLE},
            Security::{
                Authorization::{
                    ConvertSidToStringSidW, ConvertStringSecurityDescriptorToSecurityDescriptorW,
                    SDDL_REVISION_1,
                },
                GetTokenInformation, TokenUser, PSECURITY_DESCRIPTOR, SECURITY_ATTRIBUTES,
                TOKEN_QUERY, TOKEN_USER,
            },
            Storage::FileSystem::CreateDirectoryW,
            System::Threading::{GetCurrentProcess, OpenProcessToken},
        };
        // SAFETY: every out parameter is a valid local; the TOKEN_USER
        // buffer is 8-aligned and as long as the size query asked; the SID
        // string and the descriptor are freed with `LocalFree` once used,
        // and the descriptor outlives the `CreateDirectoryW` call.
        unsafe {
            let mut token: HANDLE = std::ptr::null_mut();
            assert_ne!(
                OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token),
                0
            );
            let mut needed = 0u32;
            GetTokenInformation(token, TokenUser, std::ptr::null_mut(), 0, &mut needed);
            let mut buffer = vec![0u64; (needed as usize).div_ceil(8)];
            let read = GetTokenInformation(
                token,
                TokenUser,
                buffer.as_mut_ptr().cast(),
                needed,
                &mut needed,
            );
            CloseHandle(token);
            assert_ne!(read, 0);
            let sid = (*buffer.as_ptr().cast::<TOKEN_USER>()).User.Sid;
            let mut text: *mut u16 = std::ptr::null_mut();
            assert_ne!(ConvertSidToStringSidW(sid, &mut text), 0);
            let len = (0..).take_while(|&i| *text.add(i) != 0).count();
            let user = String::from_utf16(std::slice::from_raw_parts(text, len)).unwrap();
            LocalFree(text.cast());
            let sddl: Vec<u16> = format!("O:{user}D:P(A;OICI;FA;;;{user})")
                .encode_utf16()
                .chain([0])
                .collect();
            let mut descriptor: PSECURITY_DESCRIPTOR = std::ptr::null_mut();
            assert_ne!(
                ConvertStringSecurityDescriptorToSecurityDescriptorW(
                    sddl.as_ptr(),
                    SDDL_REVISION_1,
                    &mut descriptor,
                    std::ptr::null_mut(),
                ),
                0
            );
            let attributes = SECURITY_ATTRIBUTES {
                nLength: std::mem::size_of::<SECURITY_ATTRIBUTES>() as u32,
                lpSecurityDescriptor: descriptor,
                bInheritHandle: 0,
            };
            let wide: Vec<u16> = path.as_os_str().encode_wide().chain([0]).collect();
            let created = CreateDirectoryW(wide.as_ptr(), &attributes);
            LocalFree(descriptor);
            assert_ne!(created, 0, "the test home could not be created");
        }
    }

    impl Drop for TempHome {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    /// The temp directory in its long, non-verbatim form (`X:\…`): the
    /// secure root's base rule takes that, as the agent's own
    /// `tests/support::new_home` does.
    fn temp_base() -> PathBuf {
        let long = fs::canonicalize(std::env::temp_dir()).unwrap();
        let text = long.to_str().unwrap();
        match text.strip_prefix(r"\\?\") {
            Some(rest) if rest.as_bytes().get(1) == Some(&b':') => PathBuf::from(rest),
            _ => panic!("unexpected temp directory form"),
        }
    }

    pub(crate) const PERIOD: Duration = Duration::from_millis(100);

    /// The four scan roots, all inside `home`.
    pub(crate) fn roots(home: &Path) -> ScanRoots {
        let profile = home.join("profile");
        ScanRoots {
            xdg_data_home: profile.join(".local").join("share"),
            config_dir: home.join("roaming"),
            data_dir: home.join("local"),
            home: profile,
        }
    }

    pub(crate) fn inputs(home: &Path, clock: &Arc<TestClock>, settings: ScannerSettings) -> Inputs {
        inputs_with(home, Arc::clone(clock) as Arc<dyn Clock>, settings)
    }

    fn inputs_with(home: &Path, clock: Arc<dyn Clock>, settings: ScannerSettings) -> Inputs {
        Inputs {
            trusted_home: home.to_path_buf(),
            state_suffix: TEST_SUFFIX,
            roots: roots(home),
            settings,
            pricing_source: home.join("pricing-src"),
            pricing_period: PERIOD,
            clock,
        }
    }

    /// Starts `agent` over fixed inputs below `home`.
    pub(crate) fn start_at(
        agent: &Arc<RemoteAgent>,
        home: &Path,
        clock: &Arc<TestClock>,
        settings: ScannerSettings,
    ) -> Value {
        let (home, clock) = (home.to_path_buf(), Arc::clone(clock));
        agent
            .start(Arc::new(move || {
                Ok(inputs(&home, &clock, settings.clone()))
            }))
            .unwrap()
    }

    fn start(
        agent: &Arc<RemoteAgent>,
        home: &TempHome,
        clock: &Arc<TestClock>,
        settings: ScannerSettings,
    ) -> Value {
        start_at(agent, &home.0, clock, settings)
    }

    pub(crate) fn free_port() -> u16 {
        TcpListener::bind(("127.0.0.1", 0))
            .unwrap()
            .local_addr()
            .unwrap()
            .port()
    }

    /// Two different free ports (`free_port` releases its listener, so two
    /// calls can return the same one).
    fn two_ports() -> (u16, u16) {
        let first = free_port();
        let second = std::iter::repeat_with(free_port)
            .find(|port| *port != first)
            .expect("an endless iterator");
        (first, second)
    }

    fn listening(port: u16) -> bool {
        TcpStream::connect(("127.0.0.1", port)).is_ok()
    }

    fn epoch(agent: &RemoteAgent) -> u64 {
        read(&agent.running).as_ref().unwrap().epoch
    }

    fn pull(receiver: &RemoteAgent, producer: &[u8; 32]) -> Result<(), PullError> {
        let query = ReportQuery {
            endpoint: Endpoint::Agents,
            start_date: "2040-01-01".into(),
            end_date_exclusive: "2040-01-02".into(),
            timezone: "UTC".into(),
            clients: Vec::new(),
        };
        let running = read(&receiver.running);
        running
            .as_ref()
            .unwrap()
            .agent
            .runtime()
            .pull(producer, &query)
            .map(drop)
    }

    fn claude_root(home: &TempHome) -> ScannerSettings {
        let root = home.0.join("extra").join("projects");
        fs::create_dir_all(&root).unwrap();
        ScannerSettings {
            extra_scan_paths: BTreeMap::from([("claude".to_string(), vec![root])]),
            ..ScannerSettings::default()
        }
    }

    pub(crate) fn digest(agent: &RemoteAgent) -> String {
        agent.scope_disclosure().unwrap()["ownerInputsSha256"]
            .as_str()
            .unwrap()
            .to_owned()
    }

    /// Offer → claim → SAS → approve → poll → peers → pull, through two
    /// in-process agents (no global); then a changed scan input restarts the
    /// producer, its scope is no longer approved and the existing grant's
    /// report request is refused; approving the new scope supersedes the
    /// grant; then revoke.
    #[test]
    fn two_agents_pair_a_changed_scope_is_refused_and_revoke_ends_the_grant() {
        let clock = TestClock::new();
        let (producer_home, receiver_home) = (TempHome::new(), TempHome::new());
        let (producer, receiver) = (
            Arc::new(RemoteAgent::default()),
            Arc::new(RemoteAgent::default()),
        );
        start(
            &producer,
            &producer_home,
            &clock,
            ScannerSettings::default(),
        );
        let receiver_status = start(
            &receiver,
            &receiver_home,
            &clock,
            ScannerSettings::default(),
        );

        producer
            .listen_configure("127.0.0.1", free_port(), Some("desk".into()))
            .unwrap();
        producer.scope_approve(&digest(&producer)).unwrap();
        producer.sharing_set(true).unwrap();
        let producer_status = producer.status().unwrap();
        assert_eq!(producer_status["nodeLabel"], "desk");
        assert_eq!(producer_status["scopeApproved"], true);
        assert_eq!(producer_status["sharingEnabled"], true);

        let offer = producer.offer_create(600).unwrap();
        let claim = receiver
            .pair_claim(offer["pairUrl"].as_str().unwrap())
            .unwrap();
        assert_eq!(
            claim["producerFingerprint"],
            producer_status["identityFingerprint"]
        );
        let pending = producer.pending_list().unwrap();
        let pending = &pending.as_array().unwrap()[0];
        assert_eq!(pending["sasCode"], claim["sas"], "both sides show one SAS");
        producer
            .decide(true, pending["pendingId"].as_str().unwrap(), 1)
            .unwrap();
        let paired = receiver
            .pair_poll(claim["handle"].as_u64().unwrap())
            .unwrap();
        let producer_static =
            decode_fixed_hex::<32>(paired["producerStatic"].as_str().unwrap()).unwrap();

        let peers = receiver.peers().unwrap();
        let peers = peers.as_array().unwrap();
        assert_eq!(peers.len(), 1);
        assert_eq!(
            peers[0]["fingerprint"],
            producer_status["identityFingerprint"]
        );
        assert_eq!(peers[0]["state"], "active");
        let grants = producer.grants().unwrap();
        let grant = &grants.as_array().unwrap()[0];
        assert_eq!(grant["state"], "active");
        assert_eq!(
            grant["peerFingerprint"],
            receiver_status["identityFingerprint"]
        );

        clock.advance(61);
        assert_eq!(pull(&receiver, &producer_static), Ok(()));

        // The owner adds a scan root: the next start restarts the agent.
        let before = epoch(&producer);
        let restarted = start(
            &producer,
            &producer_home,
            &clock,
            claude_root(&producer_home),
        );
        assert_ne!(epoch(&producer), before, "changed inputs must restart");
        assert_eq!(restarted["scopeApproved"], false);
        clock.advance(61);
        assert_eq!(
            pull(&receiver, &producer_static),
            Err(PullError::Fetch(FetchError::Internal)),
            "the old grant must not read the new scope"
        );
        // The same inputs again: no restart.
        let same = epoch(&producer);
        start(
            &producer,
            &producer_home,
            &clock,
            claude_root(&producer_home),
        );
        assert_eq!(epoch(&producer), same, "unchanged inputs must not restart");

        // Approving the new scope moves the generation: the grant made under
        // the old one can never read again, and the list says so (R19).
        assert_eq!(producer.grants().unwrap()[0]["state"], "active");
        producer.scope_approve(&digest(&producer)).unwrap();
        assert_eq!(producer.grants().unwrap()[0]["state"], "superseded");

        producer.revoke(grant["grantId"].as_str().unwrap()).unwrap();
        assert_eq!(producer.grants().unwrap()[0]["state"], "revoked");
        clock.advance(61);
        assert_eq!(
            pull(&receiver, &producer_static),
            Err(PullError::Fetch(FetchError::Revoked))
        );
    }

    /// R1: consent binds to the digest the caller passes, which must be the
    /// running agent's disclosed one. After a restart over other settings the
    /// old digest is refused and nothing is approved; the new disclosure's
    /// digest is accepted.
    #[test]
    fn scope_approve_refuses_the_digest_of_an_earlier_disclosure() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        let old = digest(&agent);
        let before = epoch(&agent);
        start(&agent, &home, &clock, claude_root(&home));
        assert_ne!(
            epoch(&agent),
            before,
            "control: the settings change restarted"
        );
        let new = digest(&agent);
        assert_ne!(old, new, "control: other inputs, another digest");

        assert_eq!(agent.scope_approve(&old), Err("invalid".into()));
        assert_eq!(agent.status().unwrap()["scopeApproved"], false);
        assert_eq!(agent.scope_approve(&"0".repeat(64)), Err("invalid".into()));
        assert_eq!(agent.status().unwrap()["scopeApproved"], false);

        assert_eq!(agent.scope_approve(&new), Ok(Value::Null));
        assert_eq!(agent.status().unwrap()["scopeApproved"], true);
    }

    /// R3: only an excluded path changes, through the injected settings
    /// source (no start call): within a monitor period the agent restarts
    /// (new epoch) and its scope reads unapproved. Unchanged settings over
    /// several periods restart nothing.
    #[test]
    fn an_excluded_path_change_restarts_within_one_period() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let settings = Arc::new(Mutex::new(ScannerSettings::default()));
        let make: MakeInputs = {
            let (path, clock, settings) =
                (home.0.clone(), Arc::clone(&clock), Arc::clone(&settings));
            Arc::new(move || Ok(inputs(&path, &clock, lock(&settings).clone())))
        };
        let agent = Arc::new(RemoteAgent::default());
        agent.start(make).unwrap();
        agent.scope_approve(&digest(&agent)).unwrap();
        assert_eq!(agent.status().unwrap()["scopeApproved"], true);

        let before = epoch(&agent);
        thread::sleep(PERIOD * 5);
        assert_eq!(epoch(&agent), before, "an unchanged input restarted");
        assert_eq!(agent.status().unwrap()["scopeApproved"], true);

        lock(&settings).excluded_scan_paths =
            BTreeMap::from([("claude".to_string(), vec![home.0.join("excluded")])]);
        let deadline = Instant::now() + PERIOD * 50;
        while epoch(&agent) == before {
            assert!(Instant::now() < deadline, "the change was not picked up");
            thread::sleep(PERIOD / 4);
        }
        assert_eq!(agent.status().unwrap()["scopeApproved"], false);
        assert_eq!(
            agent.scope_disclosure().unwrap()["excludedScanPaths"]["claude"]
                .as_array()
                .map(Vec::len),
            Some(1)
        );
    }

    /// R3: inputs that can no longer be built (the product's known folders
    /// failing) stop the agent within a monitor period.
    #[test]
    fn inputs_that_fail_inside_a_period_stop_the_agent() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let fail = Arc::new(AtomicBool::new(false));
        let make: MakeInputs = {
            let (path, clock, fail) = (home.0.clone(), Arc::clone(&clock), Arc::clone(&fail));
            Arc::new(move || {
                if fail.load(Ordering::SeqCst) {
                    return Err("unsafeState".into());
                }
                Ok(inputs(&path, &clock, ScannerSettings::default()))
            })
        };
        let agent = Arc::new(RemoteAgent::default());
        agent.start(make).unwrap();
        fail.store(true, Ordering::SeqCst);
        let deadline = Instant::now() + PERIOD * 50;
        while agent.status().unwrap()["running"] == true {
            assert!(Instant::now() < deadline, "the agent kept running");
            thread::sleep(PERIOD / 4);
        }
    }

    /// A start whose inputs cannot be built stops the running agent: the
    /// error comes back, no agent runs and its listener is closed.
    #[test]
    fn a_start_whose_inputs_fail_stops_the_running_agent() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        let port = free_port();
        agent.listen_configure("127.0.0.1", port, None).unwrap();
        assert!(listening(port), "control: the agent listens");
        assert_eq!(
            agent.start(Arc::new(|| Err("unsafeState".into()))),
            Err("unsafeState".into())
        );
        assert_eq!(agent.status(), Ok(json!({"running": false})));
        assert_eq!(agent.pending_list(), Err("notRunning".into()));
        assert!(!listening(port), "the stopped agent still listens");
    }

    /// A monitor period that panics (here, in `MakeInputs`) does not end
    /// the monitor: a later input change is still applied (new epoch).
    #[test]
    fn the_monitor_survives_a_panicking_period() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let settings = Arc::new(Mutex::new(ScannerSettings::default()));
        let panic_once = Arc::new(AtomicBool::new(false));
        let make: MakeInputs = {
            let (path, clock, settings, panic_once) = (
                home.0.clone(),
                Arc::clone(&clock),
                Arc::clone(&settings),
                Arc::clone(&panic_once),
            );
            Arc::new(move || {
                if panic_once.swap(false, Ordering::SeqCst) {
                    panic!("a panicking monitor period");
                }
                Ok(inputs(&path, &clock, lock(&settings).clone()))
            })
        };
        let agent = Arc::new(RemoteAgent::default());
        agent.start(make).unwrap();
        let before = epoch(&agent);
        panic_once.store(true, Ordering::SeqCst);
        let deadline = Instant::now() + PERIOD * 50;
        while panic_once.load(Ordering::SeqCst) {
            assert!(Instant::now() < deadline, "the monitor never ran");
            thread::sleep(PERIOD / 4);
        }
        // Control: the panicking period changed nothing.
        assert_eq!(epoch(&agent), before);
        lock(&settings).excluded_scan_paths =
            BTreeMap::from([("claude".to_string(), vec![home.0.join("excluded")])]);
        let deadline = Instant::now() + PERIOD * 50;
        while epoch(&agent) == before {
            assert!(
                Instant::now() < deadline,
                "the monitor did not survive the panic"
            );
            thread::sleep(PERIOD / 4);
        }
    }

    /// R2: the disclosure's values are the agent's `scope_disclosure()`
    /// fields, compared one by one, and it carries `ownerInputsSha256`.
    #[test]
    fn the_disclosure_is_the_agents_own_fields_and_digest() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let mut settings = claude_root(&home);
        settings.excluded_scan_paths =
            BTreeMap::from([("codex".to_string(), vec![home.0.join("skip")])]);
        settings.opencode_db_paths = vec![home.0.join("db").join("opencode.db")];
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, settings);
        let reply = agent.scope_disclosure().unwrap();
        let disclosure = agent
            .with_agent(|agent| agent.scope_disclosure().clone())
            .unwrap();
        assert_eq!(reply["home"], json!(disclosure.home));
        assert_eq!(reply["configDir"], json!(disclosure.config_dir));
        assert_eq!(reply["dataDir"], json!(disclosure.data_dir));
        assert_eq!(reply["xdgDataHome"], json!(disclosure.xdg_data_home));
        assert_eq!(reply["sourceCacheDir"], json!(disclosure.source_cache_dir));
        assert_eq!(reply["extraScanPaths"], json!(disclosure.extra_scan_paths));
        assert_eq!(
            reply["excludedScanPaths"],
            json!(disclosure.excluded_scan_paths)
        );
        assert_eq!(
            reply["opencodeDbPaths"],
            json!(disclosure.opencode_db_paths)
        );
        assert_eq!(
            reply["ownerInputsSha256"],
            json!(disclosure.owner_inputs_sha256)
        );
        assert_eq!(disclosure.owner_inputs_sha256.len(), 64);
        // Control: the fixtures reached the disclosure.
        assert_eq!(disclosure.extra_scan_paths["claude"].len(), 1);
        assert_eq!(disclosure.excluded_scan_paths["codex"].len(), 1);
        assert_eq!(disclosure.opencode_db_paths.len(), 1);
        assert!(disclosure.xdg_data_home.is_some());
        let keys: Vec<&String> = reply.as_object().unwrap().keys().collect();
        for key in [
            "home",
            "configDir",
            "dataDir",
            "xdgDataHome",
            "sourceCacheDir",
            "extraScanPaths",
            "excludedScanPaths",
            "opencodeDbPaths",
            "ownerInputsSha256",
            "clients",
            "envRootsSet",
            "envRoots",
        ] {
            assert!(keys.iter().any(|k| *k == key), "missing {key}");
        }
        assert_eq!(keys.len(), 12, "unexpected keys: {keys:?}");
        assert_eq!(
            reply["envRootsSet"],
            Value::Bool(!reply["envRoots"].as_array().unwrap().is_empty())
        );
    }

    /// R20: `APPDATA` / `LOCALAPPDATA` are listed only when they differ
    /// from their known folder (or it is unavailable); `HOME` and the
    /// per-client variables whenever set and non-empty. Injected lookups:
    /// no test changes the process environment.
    #[test]
    fn env_roots_lists_the_general_root_variables_that_are_set() {
        let config = PathBuf::from(r"C:\Users\u\AppData\Roaming");
        let local = PathBuf::from(r"C:\Users\u\AppData\Local");
        let roots = |env: &[(&str, &str)], config: Option<PathBuf>, local: Option<PathBuf>| {
            let env: Vec<(String, String)> = env
                .iter()
                .map(|(k, v)| (k.to_string(), v.to_string()))
                .collect();
            env_roots_from(
                &move |var| {
                    env.iter()
                        .find(|(k, _)| k == var)
                        .map(|(_, v)| OsString::from(v))
                },
                config,
                local,
            )
        };
        let general = |var: &str| json!({"client": null, "variable": var});
        let known = [
            ("APPDATA", config.to_str().unwrap()),
            ("LOCALAPPDATA", local.to_str().unwrap()),
        ];
        // The known-folder values: nothing to report.
        assert_eq!(
            roots(&known, Some(config.clone()), Some(local.clone())),
            Vec::<Value>::new()
        );
        // Values that differ, and HOME: listed, in order.
        assert_eq!(
            roots(
                &[
                    ("HOME", r"C:\h"),
                    ("APPDATA", r"D:\roaming"),
                    ("LOCALAPPDATA", r"D:\local"),
                ],
                Some(config.clone()),
                Some(local.clone())
            ),
            vec![general("HOME"), general("APPDATA"), general("LOCALAPPDATA")]
        );
        // Empty values: not listed.
        assert_eq!(
            roots(
                &[("HOME", ""), ("APPDATA", ""), ("LOCALAPPDATA", "")],
                None,
                None
            ),
            Vec::<Value>::new()
        );
        // Known folders unavailable: a set variable is listed.
        assert_eq!(
            roots(&known, None, None),
            vec![general("APPDATA"), general("LOCALAPPDATA")]
        );
        // A per-client variable carries its client.
        let xdg = roots(
            &[("XDG_DATA_HOME", r"D:\xdg")],
            Some(config.clone()),
            Some(local.clone()),
        );
        assert!(!xdg.is_empty());
        assert!(xdg
            .iter()
            .all(|root| root["variable"] == "XDG_DATA_HOME" && root["client"].is_string()));
        // This process: a variable equal to its known folder is not listed.
        let process = env_roots();
        for (var, folder) in [
            ("APPDATA", dirs::config_dir()),
            ("LOCALAPPDATA", dirs::data_local_dir()),
        ] {
            if std::env::var_os(var).map(PathBuf::from) == folder {
                assert!(!process.contains(&general(var)), "{var} listed");
            }
        }
    }

    /// R6: sharing off revokes the active grant of a device paired over
    /// `127.0.0.1`.
    #[test]
    fn sharing_off_leaves_no_active_grant() {
        let clock = TestClock::new();
        let (producer_home, receiver_home) = (TempHome::new(), TempHome::new());
        let (producer, receiver) = (
            Arc::new(RemoteAgent::default()),
            Arc::new(RemoteAgent::default()),
        );
        start(
            &producer,
            &producer_home,
            &clock,
            ScannerSettings::default(),
        );
        start(
            &receiver,
            &receiver_home,
            &clock,
            ScannerSettings::default(),
        );
        pair_two(&producer, &receiver);
        let grants = producer.grants().unwrap();
        assert_eq!(grants[0]["state"], "active", "control: paired");

        producer.sharing_set(false).unwrap();
        let grants = producer.grants().unwrap();
        let grants = grants.as_array().unwrap();
        assert!(!grants.is_empty());
        assert!(
            grants.iter().all(|grant| grant["state"] != "active"),
            "an active grant survived sharing off"
        );
        assert_eq!(producer.status().unwrap()["sharingEnabled"], false);
        assert_eq!(producer.status().unwrap()["activeGrantCount"], 0);
    }

    /// R8: the in-process entry's errors are fixed classes.
    #[test]
    fn control_errors_are_their_classes() {
        assert_eq!(control_class(ControlError::Stopped), "notRunning");
        assert_eq!(control_class(ControlError::Command), "control");
        assert_eq!(control_class(ControlError::Unavailable), "control");
        assert_eq!(control_class(ControlError::Response), "control");
        assert_eq!(control_class(ControlError::PathTooLong), "control");
        assert_eq!(control_class(ControlError::UnsafeSocket), "control");
    }

    /// Each `peers` row carries the record's `host` and `port`.
    #[test]
    fn peers_rows_carry_the_record_host_and_port() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        agent
            .with(|current| {
                let runtime = current.agent.runtime();
                let identity = runtime
                    .store()
                    .state()
                    .and_then(|state| state.identity_public())
                    .map_err(storage_class)?;
                runtime
                    .peers()
                    .upsert(PeerRecord {
                        auth_epoch: 1,
                        created_at_ms: 1,
                        expires_at_ms: i64::MAX / 2,
                        grant_id: [7; 16],
                        host: "192.0.2.10".into(),
                        initiator_static: identity,
                        node_label: Some("desk".into()),
                        pending_id: [7; 16],
                        port: 48_123,
                        responder_static: [9; 32],
                        source_scope_generation: 1,
                        state: PeerState::Active,
                    })
                    .map_err(storage_class)
            })
            .unwrap();
        let peers = agent.peers().unwrap();
        let row = &peers.as_array().unwrap()[0];
        assert_eq!(row["host"], "192.0.2.10");
        assert_eq!(row["port"], 48_123);
        assert_eq!(row["nodeLabel"], "desk");
    }

    /// The address goes through the frozen command; after a change the old
    /// port takes no connection.
    #[test]
    fn listen_configure_moves_the_listener_off_the_old_port() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        let (old, new) = two_ports();
        let status = agent.listen_configure("127.0.0.1", old, None).unwrap();
        assert_eq!(status["listening"], true);
        assert!(listening(old));
        agent.listen_configure("127.0.0.1", new, None).unwrap();
        assert!(!listening(old), "the old port still accepts");
        assert!(listening(new));
        // A host the frozen validator refuses is the class, nothing else.
        assert_eq!(
            agent.listen_configure("not a host", new, None),
            Err("invalid".into())
        );
    }

    /// A label the agent refuses (control or bidi character, blank,
    /// invisible only, over-long) is `invalid` before the address moves:
    /// the stored host, port and label, and the listener, stay as they were.
    /// A valid label moves the address and is stored in NFC; a failing
    /// address step puts the old label back.
    #[test]
    fn an_invalid_label_leaves_the_listen_address_and_label_unchanged() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        let (old, new) = two_ports();
        agent
            .listen_configure("127.0.0.1", old, Some("Desk".into()))
            .unwrap();
        let stored = || {
            let config = agent
                .with_agent(|a| a.runtime().store().state().unwrap().config)
                .unwrap();
            (config.bind_host, config.bind_port, config.node_label)
        };
        let before = stored();
        assert_eq!(before.1, old);
        let long = "x".repeat(NODE_LABEL_MAX_BYTES + 1);
        for bad in [
            "PC\u{202E}cod.exe",
            "tab\there",
            "line\u{2028}break",
            "   ",
            "\u{200B}",
            "",
            long.as_str(),
        ] {
            assert_eq!(
                agent.listen_configure("127.0.0.1", new, Some(bad.into())),
                Err("invalid".into()),
                "{bad:?}"
            );
            assert_eq!(stored(), before, "{bad:?} changed the stored config");
            assert!(listening(old), "{bad:?} moved the listener");
            assert!(!listening(new), "{bad:?} moved the listener");
        }
        // Control: a valid label moves the address; NFD input is stored as NFC.
        agent
            .listen_configure("127.0.0.1", new, Some("Cafe\u{301}".into()))
            .unwrap();
        assert_eq!(stored().1, new);
        assert_eq!(stored().2.as_deref(), Some("Caf\u{e9}"));
        assert!(listening(new) && !listening(old));
        // A refused address after a label change restores the old label.
        assert_eq!(
            agent.listen_configure("not a host", new, Some("Other".into())),
            Err("invalid".into())
        );
        assert_eq!(stored().2.as_deref(), Some("Caf\u{e9}"));
        // The label alone may be cleared.
        agent.listen_configure("127.0.0.1", new, None).unwrap();
        assert_eq!(stored().2, None);
    }

    /// Two overlapping `listen_configure` calls. The first is held between
    /// reading the stored address and writing it back; the second must wait
    /// for the `configuring` lock, so the stored address ends up the one the
    /// listener is bound to (the second call's).
    #[test]
    fn overlapping_listen_configure_calls_are_serialized() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        let (first, second) = two_ports();
        let (paused, is_paused) = std::sync::mpsc::channel::<()>();
        let (release, released) = std::sync::mpsc::channel::<()>();
        let released = Mutex::new(released);
        let once = AtomicBool::new(true);
        *lock(&agent.listen_pause) = Some(Arc::new(move || {
            if once.swap(false, Ordering::SeqCst) {
                paused.send(()).unwrap();
                let _ = lock(&released).recv_timeout(Duration::from_secs(30));
            }
        }));
        let one = Arc::clone(&agent);
        let a = thread::spawn(move || one.listen_configure("127.0.0.1", first, Some("a".into())));
        is_paused.recv_timeout(Duration::from_secs(10)).unwrap();
        assert!(
            agent.configuring.try_lock().is_err(),
            "the first call does not hold the configuring lock inside the window"
        );
        let two = Arc::clone(&agent);
        let b = thread::spawn(move || two.listen_configure("127.0.0.1", second, Some("b".into())));
        thread::sleep(Duration::from_millis(400));
        let early = b.is_finished();
        release.send(()).unwrap();
        a.join().unwrap().unwrap();
        b.join().unwrap().unwrap();
        assert!(!early, "the second call ran inside the first call's window");
        let stored = agent
            .with_agent(|a| a.runtime().store().state().unwrap().config)
            .unwrap();
        assert_eq!(
            stored.bind_port, second,
            "stored address is not the bound one"
        );
        assert!(listening(second));
        assert!(!listening(first));
    }

    /// Both pricing files are copied at start (same bytes, no temp file
    /// left); a newer source is copied again within a period, no restart.
    #[test]
    fn pricing_is_copied_at_start_and_again_when_the_source_is_newer() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let source = home.0.join("pricing-src");
        fs::create_dir(&source).unwrap();
        for name in PRICING_FILES {
            fs::write(source.join(name), format!("{{\"{name}\":1}}")).unwrap();
        }
        let agent = Arc::new(RemoteAgent::default());
        start(&agent, &home, &clock, ScannerSettings::default());
        let destination = agent.with_agent(|a| a.pricing_dir().to_path_buf()).unwrap();
        for name in PRICING_FILES {
            assert_eq!(
                fs::read(destination.join(name)).unwrap(),
                fs::read(source.join(name)).unwrap()
            );
        }
        let started = epoch(&agent);

        let updated = source.join(PRICING_FILES[0]);
        fs::write(&updated, "{\"newer\":2}").unwrap();
        fs::File::options()
            .write(true)
            .open(&updated)
            .unwrap()
            .set_modified(SystemTime::now() + Duration::from_secs(5))
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while fs::read(destination.join(PRICING_FILES[0])).unwrap() != b"{\"newer\":2}" {
            assert!(Instant::now() < deadline, "the newer source was not copied");
            thread::sleep(Duration::from_millis(20));
        }
        assert_eq!(epoch(&agent), started);
        let names: Vec<String> = fs::read_dir(&destination)
            .unwrap()
            .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
            .collect();
        assert!(
            names.iter().all(|name| !name.ends_with(".tmp")),
            "a temp file was left: {names:?}"
        );
    }

    /// A producer with an approved scope, sharing on and listening on
    /// loopback, paired to `receiver` through offer, claim, approve and
    /// poll. Returns the producer's static key.
    pub(crate) fn pair_two(producer: &RemoteAgent, receiver: &RemoteAgent) -> [u8; 32] {
        producer
            .listen_configure("127.0.0.1", free_port(), None)
            .unwrap();
        producer.scope_approve(&digest(producer)).unwrap();
        producer.sharing_set(true).unwrap();
        let offer = producer.offer_create(600).unwrap();
        let claim = receiver
            .pair_claim(offer["pairUrl"].as_str().unwrap())
            .unwrap();
        let pending = producer.pending_list().unwrap();
        producer
            .decide(true, pending[0]["pendingId"].as_str().unwrap(), 1)
            .unwrap();
        let paired = receiver
            .pair_poll(claim["handle"].as_u64().unwrap())
            .unwrap();
        decode_fixed_hex::<32>(paired["producerStatic"].as_str().unwrap()).unwrap()
    }

    /// Receiver cache files (`rcache-*`) anywhere below `dir`.
    fn receiver_cache_files(dir: &Path) -> usize {
        fs::read_dir(dir)
            .map(|entries| {
                entries
                    .flatten()
                    .map(|entry| {
                        let path = entry.path();
                        if path.is_dir() && !path.is_symlink() {
                            receiver_cache_files(&path)
                        } else {
                            usize::from(entry.file_name().to_string_lossy().starts_with("rcache-"))
                        }
                    })
                    .sum()
            })
            .unwrap_or(0)
    }

    /// Remove deletes the producer's receiver cache entries at once
    /// (`Runtime::remove_peer`). The entry comes from a real pull, so the
    /// count before is not vacuous.
    #[test]
    fn peer_remove_deletes_the_receiver_cache() {
        let clock = TestClock::new();
        let (producer_home, receiver_home) = (TempHome::new(), TempHome::new());
        let (producer, receiver) = (
            Arc::new(RemoteAgent::default()),
            Arc::new(RemoteAgent::default()),
        );
        start(
            &producer,
            &producer_home,
            &clock,
            ScannerSettings::default(),
        );
        start(
            &receiver,
            &receiver_home,
            &clock,
            ScannerSettings::default(),
        );
        let producer_static = pair_two(&producer, &receiver);
        clock.advance(61);
        assert_eq!(pull(&receiver, &producer_static), Ok(()));
        assert!(
            receiver_cache_files(&receiver_home.0) >= 1,
            "control: the pull must have written a receiver cache entry"
        );

        receiver.peer_remove(&hex_encode(&producer_static)).unwrap();
        assert_eq!(receiver_cache_files(&receiver_home.0), 0);
        assert_eq!(receiver.peers().unwrap(), json!([]));
        assert_eq!(receiver.peer_remove("not hex"), Err("invalid".into()));
    }

    /// A canonical Pair URL to `127.0.0.1:port` that expires `ttl_ms` after
    /// the clock's now, with a responder key that is not the receiver's.
    fn loopback_pair_url(clock: &TestClock, port: u16, ttl_ms: i64) -> String {
        let now_ms = clock.os_now_seconds().unwrap() * 1_000;
        syrtis_agent::security::pair_url(&syrtis_agent::security::PairOffer {
            expires_at: now_ms + ttl_ms,
            host: "127.0.0.1".into(),
            offer_id: hex_encode(&[3; 16]),
            port,
            responder_static: hex_encode(&[4; 32]),
            secret: hex_encode(&[5; 32]),
            version: syrtis_agent::security::PEER_PROTOCOL_VERSION,
        })
        .unwrap()
    }

    /// The preview answers the offer's own host and port, the reply holds
    /// exactly `host` and `port` (no secret, no offer id), and it makes no
    /// claim.
    #[test]
    fn pair_destination_is_the_offer_address_and_nothing_more() {
        let clock = TestClock::new();
        let (producer_home, receiver_home) = (TempHome::new(), TempHome::new());
        let (producer, receiver) = (
            Arc::new(RemoteAgent::default()),
            Arc::new(RemoteAgent::default()),
        );
        start(
            &producer,
            &producer_home,
            &clock,
            ScannerSettings::default(),
        );
        let port = free_port();
        producer.listen_configure("127.0.0.1", port, None).unwrap();
        producer.scope_approve(&digest(&producer)).unwrap();
        producer.sharing_set(true).unwrap();
        let offer = producer.offer_create(600).unwrap();
        let pair_url = offer["pairUrl"].as_str().unwrap();
        let parsed = parse_pair_url(pair_url, clock.os_now_seconds().unwrap() * 1_000).unwrap();

        assert_eq!(
            receiver.pair_destination(pair_url),
            Err("notRunning".into())
        );
        start(
            &receiver,
            &receiver_home,
            &clock,
            ScannerSettings::default(),
        );
        let reply = receiver.pair_destination(pair_url).unwrap();
        assert_eq!(reply, json!({"host": "127.0.0.1", "port": port}));
        let keys: Vec<&String> = reply.as_object().unwrap().keys().collect();
        assert_eq!(keys, ["host", "port"]);
        let text = reply.to_string();
        assert!(
            !text.contains(&parsed.secret),
            "the secret must not be answered"
        );
        assert!(!text.contains(offer["offerId"].as_str().unwrap()));
        assert!(!text.contains(&parsed.offer_id));
        assert!(
            lock(&receiver.claims).open.is_empty(),
            "a preview claims nothing"
        );
        assert!(
            producer
                .pending_list()
                .unwrap()
                .as_array()
                .unwrap()
                .is_empty(),
            "the producer saw no claim"
        );
    }

    /// Expired, garbage and over-long URLs are their classes, and none of
    /// them makes a claim.
    #[test]
    fn pair_destination_refusals_are_their_classes() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let receiver = Arc::new(RemoteAgent::default());
        start(&receiver, &home, &clock, ScannerSettings::default());
        let expired = loopback_pair_url(&clock, 47_000, 60_000);
        assert!(receiver.pair_destination(&expired).is_ok(), "control");
        clock.advance(120);
        assert_eq!(receiver.pair_destination(&expired), Err("expired".into()));
        assert_eq!(
            receiver.pair_destination("not a pair url"),
            Err("invalidOffer".into())
        );
        let tampered = loopback_pair_url(&clock, 47_000, 60_000).replacen("7b", "7B", 1);
        assert_eq!(
            receiver.pair_destination(&tampered),
            Err("invalidOffer".into())
        );
        let long = format!(
            "syrtis://pair?offer={}",
            "0".repeat(syrtis_agent::security::PAIR_URL_RAW_MAX)
        );
        assert_eq!(receiver.pair_destination(&long), Err("invalidOffer".into()));
        let claims = lock(&receiver.claims);
        assert!(
            claims.open.is_empty() && claims.next == 0,
            "no claim was made"
        );
    }

    /// A preview of a URL to a live `127.0.0.1` listener makes no
    /// connection; control: `pair_claim` with the same URL connects.
    #[test]
    fn pair_destination_never_connects() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let receiver = Arc::new(RemoteAgent::default());
        start(&receiver, &home, &clock, ScannerSettings::default());
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        listener.set_nonblocking(true).unwrap();
        let port = listener.local_addr().unwrap().port();
        let pair_url = loopback_pair_url(&clock, port, 600_000);

        receiver.pair_destination(&pair_url).unwrap();
        // A completed connect waits in the backlog whether or not it was
        // accepted, so a nonblocking accept sees any the preview made. (The
        // `Ok` here is a TCP stream, never an offer.)
        assert_eq!(
            listener.accept().map(drop).unwrap_err().kind(),
            std::io::ErrorKind::WouldBlock,
            "the preview must not connect"
        );

        let accepted = Arc::new(AtomicUsize::new(0));
        let done = Arc::new(AtomicBool::new(false));
        let acceptor = {
            let (accepted, done) = (Arc::clone(&accepted), Arc::clone(&done));
            thread::spawn(move || {
                while !done.load(Ordering::SeqCst) {
                    match listener.accept() {
                        // Dropped at once: the claim fails on its transport.
                        Ok(_) => {
                            accepted.fetch_add(1, Ordering::SeqCst);
                        }
                        Err(_) => thread::sleep(Duration::from_millis(5)),
                    }
                }
            })
        };
        assert!(receiver.pair_claim(&pair_url).is_err());
        done.store(true, Ordering::SeqCst);
        acceptor.join().unwrap();
        assert!(
            accepted.load(Ordering::SeqCst) >= 1,
            "control: the claim with the same URL connects"
        );
    }

    /// R7: a panic inside a `tb_remote_*` body is the fixed class, without
    /// its text.
    #[test]
    fn a_panic_is_the_fixed_class_without_its_text() {
        let raw = ffi("tb_remote_test", || panic!("C:\\secret-path"));
        let reply = take(raw);
        assert_eq!(reply, json!({"ok": false, "err": "panic"}));
    }

    fn take(raw: *mut c_char) -> Value {
        let text = unsafe { CStr::from_ptr(raw) }.to_str().unwrap().to_owned();
        unsafe { crate::tb_free(raw) };
        serde_json::from_str(&text).unwrap()
    }

    fn c(text: &str) -> CString {
        CString::new(text).unwrap()
    }

    /// Serializes the tests that use the process-wide [`AGENT`].
    static GLOBAL: Mutex<()> = Mutex::new(());

    /// R10: in a test build `tb_remote_start(NULL)` is `homeRequired` (the
    /// product roots are never read) and any override home is
    /// `unsupported`, nothing opened.
    #[test]
    fn ffi_start_without_injected_inputs_opens_nothing() {
        let _global = lock(&GLOBAL);
        let ffi = crate::remote_ffi::tb_remote_start;
        assert_eq!(
            take(unsafe { ffi(std::ptr::null()) }),
            json!({"ok": false, "err": "homeRequired"})
        );
        let home = TempHome::new();
        let path = c(home.0.to_str().unwrap());
        assert_eq!(
            take(unsafe { ffi(path.as_ptr()) }),
            json!({"ok": false, "err": "unsupported"})
        );
        assert_eq!(
            fs::read_dir(&home.0).unwrap().count(),
            0,
            "the override home was written"
        );
        assert_eq!(
            take(crate::remote_ffi::tb_remote_status()),
            json!({"ok": true, "data": {"running": false}})
        );
    }

    /// Every one of the 19 entry points returns an envelope with the
    /// documented data or a fixed class, through the process-wide agent
    /// started over injected inputs: first as a producer (a local receiver
    /// claims), then as a receiver (a local producer offers).
    #[test]
    fn ffi_entry_points_answer_envelopes() {
        let _global = lock(&GLOBAL);
        use crate::remote_ffi as f;
        let clock = TestClock::new();
        let (global_home, other_home) = (TempHome::new(), TempHome::new());
        // Only `ok` and `err` are compared, so a failing assertion never
        // prints a whole reply (an offer-bearing one included).
        let fixed = |value: &Value, class: &str| {
            assert_eq!(value["ok"], false);
            assert_eq!(value["err"], class);
        };

        // Stopped: every call that needs an agent is `notRunning`.
        fixed(&take(f::tb_remote_pending_list()), "notRunning");
        fixed(&take(f::tb_remote_scope_disclosure()), "notRunning");
        fixed(&take(f::tb_remote_grants()), "notRunning");
        fixed(&take(f::tb_remote_peers()), "notRunning");
        fixed(&take(f::tb_remote_sharing_set(1)), "notRunning");
        fixed(&take(f::tb_remote_offer_create(600)), "notRunning");
        fixed(&take(f::tb_remote_pair_poll(1)), "invalid");

        // The global agent as producer.
        start_at(&AGENT, &global_home.0, &clock, ScannerSettings::default());
        let status = take(f::tb_remote_status());
        assert_eq!(status["ok"], true);
        assert_eq!(status["data"]["running"], true);
        assert_eq!(status["data"]["sharingEnabled"], false);
        assert_eq!(status["data"]["listening"], false);
        assert_eq!(status["data"]["scopeApproved"], false);
        let host = c("127.0.0.1");
        let label = c("desk");
        let port = i32::from(free_port());
        let configured =
            take(unsafe { f::tb_remote_listen_configure(host.as_ptr(), port, label.as_ptr()) });
        assert_eq!(configured["data"]["listening"], true);
        assert_eq!(configured["data"]["nodeLabel"], "desk");
        fixed(
            &take(unsafe { f::tb_remote_listen_configure(host.as_ptr(), -1, std::ptr::null()) }),
            "invalid",
        );
        let disclosure = take(f::tb_remote_scope_disclosure());
        let shown = c(disclosure["data"]["ownerInputsSha256"].as_str().unwrap());
        let wrong = c(&"0".repeat(64));
        fixed(
            &take(unsafe { f::tb_remote_scope_approve(wrong.as_ptr()) }),
            "invalid",
        );
        fixed(
            &take(unsafe { f::tb_remote_scope_approve(std::ptr::null()) }),
            "invalid",
        );
        assert_eq!(
            take(unsafe { f::tb_remote_scope_approve(shown.as_ptr()) }),
            json!({"ok": true, "data": null})
        );
        assert_eq!(
            take(f::tb_remote_sharing_set(1)),
            json!({"ok": true, "data": null})
        );
        // An offer cancelled, then one claimed and rejected, then one approved.
        let cancelled = take(f::tb_remote_offer_create(600));
        let offer_id = c(cancelled["data"]["offerId"].as_str().unwrap());
        assert_eq!(
            take(unsafe { f::tb_remote_offer_cancel(offer_id.as_ptr()) }),
            json!({"ok": true, "data": null})
        );
        fixed(
            &take(unsafe { f::tb_remote_offer_cancel(offer_id.as_ptr()) }),
            "invalid",
        );
        let receiver = Arc::new(RemoteAgent::default());
        start_at(&receiver, &other_home.0, &clock, ScannerSettings::default());
        for approve in [false, true] {
            let offer = take(f::tb_remote_offer_create(600));
            assert_eq!(offer["ok"], true);
            let claim = receiver
                .pair_claim(offer["data"]["pairUrl"].as_str().unwrap())
                .unwrap();
            let pending = take(f::tb_remote_pending_list());
            let pending = &pending["data"][0];
            assert_eq!(pending["sasCode"], claim["sas"]);
            let pending_id = c(pending["pendingId"].as_str().unwrap());
            let decided = if approve {
                take(unsafe { f::tb_remote_approve(pending_id.as_ptr(), 1) })
            } else {
                take(unsafe { f::tb_remote_reject(pending_id.as_ptr(), 1) })
            };
            assert_eq!(decided["ok"], true);
            let polled = receiver.pair_poll(claim["handle"].as_u64().unwrap());
            if approve {
                assert!(polled.is_ok());
            } else {
                assert_eq!(polled, Err("rejected".into()));
            }
        }
        let grants = take(f::tb_remote_grants());
        assert_eq!(grants["data"][0]["state"], "active");
        let grant_id = c(grants["data"][0]["grantId"].as_str().unwrap());
        assert_eq!(
            take(unsafe { f::tb_remote_revoke(grant_id.as_ptr()) }),
            json!({"ok": true, "data": null})
        );
        assert_eq!(take(f::tb_remote_grants())["data"][0]["state"], "revoked");
        assert_eq!(
            take(f::tb_remote_sharing_set(0)),
            json!({"ok": true, "data": null})
        );
        assert_eq!(
            take(f::tb_remote_stop()),
            json!({"ok": true, "data": {"running": false}})
        );
        drop(receiver);

        // The global agent as receiver of a local producer.
        let producer = Arc::new(RemoteAgent::default());
        start_at(&producer, &other_home.0, &clock, ScannerSettings::default());
        start_at(&AGENT, &global_home.0, &clock, ScannerSettings::default());
        let producer_port = free_port();
        producer
            .listen_configure("127.0.0.1", producer_port, None)
            .unwrap();
        producer.scope_approve(&digest(&producer)).unwrap();
        producer.sharing_set(true).unwrap();
        let offer = producer.offer_create(600).unwrap();
        let url = c(offer["pairUrl"].as_str().unwrap());
        let destination = take(unsafe { f::tb_remote_pair_destination(url.as_ptr()) });
        assert_eq!(destination["ok"], true);
        assert_eq!(destination["err"], Value::Null);
        assert_eq!(destination["data"]["host"], "127.0.0.1");
        assert_eq!(destination["data"]["port"], producer_port);
        let garbage = c("not a pair url");
        fixed(
            &take(unsafe { f::tb_remote_pair_destination(garbage.as_ptr()) }),
            "invalidOffer",
        );
        fixed(
            &take(unsafe { f::tb_remote_pair_claim(garbage.as_ptr()) }),
            "invalidOffer",
        );
        let claim = take(unsafe { f::tb_remote_pair_claim(url.as_ptr()) });
        assert_eq!(claim["ok"], true);
        let pending = producer.pending_list().unwrap();
        producer
            .decide(true, pending[0]["pendingId"].as_str().unwrap(), 1)
            .unwrap();
        let handle = claim["data"]["handle"].as_i64().unwrap();
        let paired = take(f::tb_remote_pair_poll(handle));
        assert_eq!(paired["ok"], true);
        fixed(&take(f::tb_remote_pair_poll(handle)), "invalid");
        fixed(&take(f::tb_remote_pair_poll(-1)), "invalid");
        let peers = take(f::tb_remote_peers());
        assert_eq!(peers["data"][0]["state"], "active");
        let producer_static = c(paired["data"]["producerStatic"].as_str().unwrap());
        assert_eq!(
            take(unsafe { f::tb_remote_peer_remove(producer_static.as_ptr()) }),
            json!({"ok": true, "data": null})
        );
        assert_eq!(take(f::tb_remote_peers()), json!({"ok": true, "data": []}));
        assert_eq!(
            take(f::tb_remote_stop()),
            json!({"ok": true, "data": {"running": false}})
        );
        assert_eq!(
            take(f::tb_remote_stop()),
            json!({"ok": true, "data": {"running": false}})
        );
    }

    /// A monitor that cannot be spawned leaves no agent running (a later
    /// input change would never be applied) and start answers a fixed
    /// class; the next start spawns it.
    #[test]
    fn a_monitor_that_cannot_start_stops_the_agent() {
        let clock = TestClock::new();
        let home = TempHome::new();
        let agent = Arc::new(RemoteAgent::default());
        agent.fail_monitor_spawn.store(true, Ordering::SeqCst);
        let (path, time) = (home.0.clone(), Arc::clone(&clock));
        assert_eq!(
            agent.start(Arc::new(move || Ok(inputs(
                &path,
                &time,
                ScannerSettings::default()
            )))),
            Err("internal".into())
        );
        assert_eq!(agent.status(), Ok(json!({"running": false})));
        assert_eq!(
            start(&agent, &home, &clock, ScannerSettings::default())["running"],
            true
        );
        assert!(agent.monitor.load(Ordering::SeqCst));
    }

    /// A clock whose next `sleep`, once armed, signals and then waits to be
    /// released: holds `pair_poll` inside its wait.
    struct GateClock {
        inner: Arc<TestClock>,
        armed: AtomicBool,
        entered: Mutex<std::sync::mpsc::Sender<()>>,
        release: Mutex<std::sync::mpsc::Receiver<()>>,
    }

    impl Clock for GateClock {
        fn monotonic(&self) -> Duration {
            self.inner.monotonic()
        }
        fn os_now_seconds(&self) -> Result<i64, StorageError> {
            self.inner.os_now_seconds()
        }
        fn sleep(&self, duration: Duration) {
            if self.armed.swap(false, Ordering::SeqCst) {
                let _ = lock(&self.entered).send(());
                let _ = lock(&self.release).recv();
            }
            self.inner.sleep(duration);
        }
    }

    /// R18: the receiver is restarted on another home while `pair_poll`
    /// waits; the completed grant belongs to the identity that claimed it,
    /// so the poll answers `identityChanged` and the new home's peer store
    /// gets nothing.
    #[test]
    fn a_poll_that_ends_after_a_restart_on_another_home_writes_nothing() {
        let clock = TestClock::new();
        let (producer_home, first, second) = (TempHome::new(), TempHome::new(), TempHome::new());
        let producer = Arc::new(RemoteAgent::default());
        start(
            &producer,
            &producer_home,
            &clock,
            ScannerSettings::default(),
        );
        producer
            .listen_configure("127.0.0.1", free_port(), None)
            .unwrap();
        producer.sharing_set(true).unwrap();

        let (entered_tx, entered_rx) = std::sync::mpsc::channel();
        let (release_tx, release_rx) = std::sync::mpsc::channel();
        let gate = Arc::new(GateClock {
            inner: Arc::clone(&clock),
            armed: AtomicBool::new(false),
            entered: Mutex::new(entered_tx),
            release: Mutex::new(release_rx),
        });
        let receiver = Arc::new(RemoteAgent::default());
        let on = |home: &TempHome| -> MakeInputs {
            let (home, gate) = (home.0.clone(), Arc::clone(&gate));
            Arc::new(move || {
                Ok(inputs_with(
                    &home,
                    Arc::clone(&gate) as Arc<dyn Clock>,
                    ScannerSettings::default(),
                ))
            })
        };
        receiver.start(on(&first)).unwrap();
        let first_identity = receiver.status().unwrap()["identityFingerprint"].clone();

        let offer = producer.offer_create(600).unwrap();
        let claim = receiver
            .pair_claim(offer["pairUrl"].as_str().unwrap())
            .unwrap();
        let pending = producer.pending_list().unwrap()[0]["pendingId"]
            .as_str()
            .unwrap()
            .to_owned();
        producer.decide(true, &pending, 1).unwrap();

        gate.armed.store(true, Ordering::SeqCst);
        let polling = Arc::clone(&receiver);
        let handle = claim["handle"].as_u64().unwrap();
        let poll = thread::spawn(move || polling.pair_poll(handle));
        entered_rx
            .recv_timeout(Duration::from_secs(5))
            .expect("the poll did not reach its wait");
        receiver.start(on(&second)).unwrap();
        assert_ne!(
            receiver.status().unwrap()["identityFingerprint"],
            first_identity
        );
        release_tx.send(()).unwrap();

        assert_eq!(poll.join().unwrap(), Err("identityChanged".into()));
        assert_eq!(
            receiver.peers(),
            Ok(json!([])),
            "the second home got the grant"
        );
    }
}
