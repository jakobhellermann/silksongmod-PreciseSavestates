use clap::{Parser, ValueEnum};
use crossterm::event::{self, Event, KeyCode, KeyEventKind, KeyModifiers};
use crossterm::terminal::{disable_raw_mode, enable_raw_mode};
use serde_json::{Map, Value};
use std::io::Write;
use std::sync::{Arc, Mutex};

/// Interactively bisect a savestate file to find what breaks loading it.
///
/// Rewrites the savestate in place for each step; load it in-game, then answer
/// g (good), b (broken) or u (undo). The original is restored on exit and on Ctrl+C.
#[derive(Parser)]
struct Args {
    /// Savestate JSON to bisect (edited in place)
    savestate: String,

    /// Bisect the Data fields of this component (unique Path suffix) instead of which
    /// snapshots get applied
    #[arg(conflicts_with_all = ["only", "kinds"])]
    component: Option<String>,

    /// Apply only snapshots (of --kinds) whose Path contains one of these substrings, once, for a
    /// manual test
    #[arg(long, value_delimiter = ',', num_args = 1..)]
    only: Option<Vec<String>>,

    /// Snapshot kinds to bisect / filter; other kinds are always applied unchanged
    #[arg(long, value_enum, value_delimiter = ',', num_args = 1..,
          default_values_t = [Kind::Component, Kind::Gameobject, Kind::Fsm, Kind::Audio])]
    kinds: Vec<Kind>,
}

#[derive(Clone, Copy, PartialEq, Eq, ValueEnum)]
enum Kind {
    Component,
    Gameobject,
    Fsm,
    Audio,
}

impl Kind {
    fn key(self) -> &'static str {
        match self {
            Kind::Component => "ComponentSnapshots",
            Kind::Gameobject => "GameObjectSnapshots",
            Kind::Fsm => "FsmSnapshots",
            Kind::Audio => "AudioTableSnapshots",
        }
    }

    fn describe(self, v: &Value) -> String {
        let s = |k: &str| v[k].as_str().unwrap_or("<none>");
        match self {
            Kind::Component => format!("[component] {}", s("Path")),
            Kind::Gameobject => format!("[gameobject] {}", s("Path")),
            Kind::Fsm => format!("[fsm] {} ({})", s("Path"), s("FsmName")),
            Kind::Audio => format!("[audio] {}", s("Name")),
        }
    }
}

type Item = (Kind, Value);

fn collect_items(original: &Value, kinds: &[Kind]) -> Vec<Item> {
    kinds
        .iter()
        .flat_map(|&kind| {
            original[kind.key()]
                .as_array()
                .into_iter()
                .flatten()
                .map(move |v| (kind, v.clone()))
        })
        .collect()
}

// Replaces the arrays of all `kinds` with the items of `subset`; other kinds stay as in `original`.
fn with_items(original: &Value, kinds: &[Kind], subset: &[Item]) -> Value {
    let mut modified = original.clone();
    for &kind in kinds {
        let items = subset.iter().filter(|(k, _)| *k == kind).map(|(_, v)| v.clone()).collect();
        modified[kind.key()] = Value::Array(items);
    }
    modified
}

fn main() {
    let args = Args::parse();
    let path = args.savestate;

    let original_content = std::fs::read_to_string(&path).expect("failed to read file");
    let original: Value = serde_json::from_str(&original_content).expect("invalid JSON");

    let restore_on_ctrlc: Arc<Mutex<Option<(String, String)>>> =
        Arc::new(Mutex::new(Some((path.clone(), original_content.clone()))));
    let ctrlc_slot = restore_on_ctrlc.clone();
    ctrlc::set_handler(move || {
        disable_raw_mode().ok();
        if let Some((path, content)) = ctrlc_slot.lock().unwrap().take() {
            std::fs::write(&path, &content).ok();
            eprintln!("\nRestored original savestate.");
        }
        std::process::exit(130);
    })
    .expect("failed to set Ctrl+C handler");

    match (args.component, args.only) {
        // Lets you check an exact hypothesis set (e.g. minimize a backtrack result: does the
        // trigger group break *without* the context the backtrack happened to keep?).
        (_, Some(only)) => {
            let patterns: Vec<&str> = only.iter().map(|s| s.trim()).filter(|s| !s.is_empty()).collect();
            apply_only(&path, &original, &args.kinds, &patterns, &restore_on_ctrlc, &original_content);
        }
        (Some(component), None) => bisect_data(&path, &original, &component, &restore_on_ctrlc, &original_content),
        (None, None) => bisect_snapshots(&path, &original, &args.kinds, &restore_on_ctrlc, &original_content),
    }
}

fn apply_only(
    path: &str,
    original: &Value,
    kinds: &[Kind],
    patterns: &[&str],
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    let all = collect_items(original, kinds);
    let describe = |(kind, v): &Item| kind.describe(v);

    let kept: Vec<Item> = all
        .iter()
        .filter(|item| patterns.iter().any(|pat| describe(item).contains(pat)))
        .cloned()
        .collect();

    println!("Applying only {} of {} snapshots:", kept.len(), all.len());
    for item in &kept {
        println!("    {}", describe(item));
    }
    for pat in patterns {
        if !all.iter().any(|item| describe(item).contains(pat)) {
            println!("  ! no snapshot matched '{pat}'");
        }
    }

    write_json(path, &with_items(original, kinds, &kept));

    print!("\nWritten. Load and test, then press any key to restore original… ");
    std::io::stdout().flush().unwrap();
    if read_any().is_none() {
        ctrlc_exit(restore_on_ctrlc, original_content, path);
    }
    println!();
    restore_on_ctrlc.lock().unwrap().take();
    std::fs::write(path, original_content).expect("failed to restore original");
    println!("Original savestate restored.");
}

// Mode 1: bisect which snapshots (of the selected kinds) get applied at all.
fn bisect_snapshots(
    path: &str,
    original: &Value,
    kinds: &[Kind],
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    let items = collect_items(original, kinds);

    let kind_names: Vec<&str> = kinds.iter().map(|k| k.key()).collect();
    println!("Bisecting {} snapshots ({})", items.len(), kind_names.join(", "));
    println!("g = good, b = broken, u = undo\n");

    let write_subset = |target: &str, subset: &[Item]| write_json(target, &with_items(original, kinds, subset));
    let describe = |(kind, v): &Item| kind.describe(v);

    run_bisect(
        path,
        items,
        "snapshot",
        &describe,
        &write_subset,
        restore_on_ctrlc,
        original_content,
    );
}

// Mode 2: bisect the Data fields of a single component snapshot.
fn bisect_data(
    path: &str,
    original: &Value,
    component: &str,
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    let snapshots = original["ComponentSnapshots"]
        .as_array()
        .expect("no ComponentSnapshots array");

    // `component` is a Path suffix: "HealthManager" or "@HealthManager" matches by component type
    // (first hit), a fuller "Farmer Catcher (1)@HealthManager" disambiguates. Ambiguous → list + exit.
    let matches: Vec<usize> = snapshots
        .iter()
        .enumerate()
        .filter(|(_, s)| s["Path"].as_str().map_or(false, |p| p.ends_with(component)))
        .map(|(i, _)| i)
        .collect();
    let hero_idx = match matches.as_slice() {
        [] => panic!("no snapshot path ends with '{component}'"),
        [i] => *i,
        many => {
            eprintln!("'{component}' is ambiguous — {} snapshots match:", many.len());
            for &i in many {
                eprintln!("    {}", snapshots[i]["Path"].as_str().unwrap_or("<no Path>"));
            }
            eprintln!("Pass a longer, unique Path suffix.");
            std::process::exit(1);
        }
    };

    let all_fields: Vec<(String, Value)> = snapshots[hero_idx]["Data"]
        .as_object()
        .expect("Data is not an object")
        .iter()
        .map(|(k, v)| (k.clone(), v.clone()))
        .collect();

    println!("Bisecting {} fields in {component}", all_fields.len());
    println!("g = good, b = broken, u = undo\n");

    let write_subset = |target: &str, subset: &[(String, Value)]| {
        let mut modified = original.clone();
        let data: Map<String, Value> = subset.iter().map(|(k, v)| (k.clone(), v.clone())).collect();
        modified["ComponentSnapshots"][hero_idx]["Data"] = Value::Object(data);
        write_json(target, &modified);
    };
    let describe = |f: &(String, Value)| f.0.clone();

    run_bisect(
        path,
        all_fields,
        "field",
        &describe,
        &write_subset,
        restore_on_ctrlc,
        original_content,
    );
}

// Generic binary-search bisect over a list of items. `write_subset` writes a test savestate that
// applies only the given subset of items; `describe` names an item for the culprit report. Shared
// by both modes.
fn run_bisect<T: Clone>(
    path: &str,
    all: Vec<T>,
    unit: &str,
    describe: &impl Fn(&T) -> String,
    write_subset: &impl Fn(&str, &[T]),
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    // Smallest known-broken subset seen across the whole run (full test, forward `b`s, backtrack
    // breaks). Written incrementally to a sibling file so it survives Ctrl+C — the minimal
    // reproducing savestate to minimize further from.
    let copy_path = min_broken_path(path);
    let mut smallest_broken: Option<Vec<T>> = None;

    write_subset(path, &all);
    print!("Full ({} {unit}s) written. Load and test — ok? [g/b]: ", all.len());
    std::io::stdout().flush().unwrap();
    match read_ynu() {
        None => ctrlc_exit(restore_on_ctrlc, original_content, path),
        Some(Ok(true)) => {
            println!("g");
            restore_on_ctrlc.lock().unwrap().take();
            std::fs::write(path, original_content).expect("failed to restore original");
            println!("Full savestate is ok — nothing to bisect.");
            return;
        }
        _ => println!("b"),
    }
    record_broken(&mut smallest_broken, &all, &copy_path, write_subset);

    write_subset(path, &[]);
    print!("Baseline (none of the {} {unit}s) written — ok? [g/b]: ", all.len());
    std::io::stdout().flush().unwrap();
    match read_ynu() {
        None => ctrlc_exit(restore_on_ctrlc, original_content, path),
        Some(Ok(true)) => println!("g"),
        _ => {
            println!("b");
            restore_on_ctrlc.lock().unwrap().take();
            std::fs::write(path, original_content).expect("failed to restore original");
            println!("Broken even without any of these {unit}s — the cause is outside the bisected set.");
            println!("Original savestate restored.");
            return;
        }
    }

    // Minimal culprit set via repeated smallest-broken-prefix search (handles combinations, not just
    // single culprits). Invariant: `required ∪ pool[..lo]` is good, `required ∪ pool[..hi]` is
    // broken; then pool[hi-1] is necessary and becomes required. Assumes adding items never fixes it.
    #[derive(Clone)]
    struct State<T> {
        required: Vec<T>,
        pool: Vec<T>,
        lo: usize,
        hi: usize,
    }
    let n = all.len();
    let mut state = State { required: vec![], pool: all, lo: 0, hi: n };
    let mut history: Vec<State<T>> = vec![];

    let culprits = loop {
        if state.hi - state.lo > 1 {
            let mid = (state.lo + state.hi) / 2;
            let subset: Vec<T> = state.required.iter().chain(&state.pool[..mid]).cloned().collect();
            write_subset(path, &subset);
            print!(
                "[{} found, searching {} candidates] {} required + first {mid} — ok? [g/b/u]: ",
                state.required.len(),
                state.hi - state.lo,
                state.required.len(),
            );
            std::io::stdout().flush().unwrap();

            let Some(answer) = read_answer(&mut history, &mut state, restore_on_ctrlc, original_content, path) else {
                continue;
            };
            if !answer {
                record_broken(&mut smallest_broken, &subset, &copy_path, write_subset);
            }
            if answer {
                state.lo = mid;
            } else {
                state.hi = mid;
            }
            continue;
        }

        let found = state.pool[state.hi - 1].clone();
        println!("  → required: {}", describe(&found));
        state.required.push(found);
        state.pool.truncate(state.hi - 1);

        if state.pool.is_empty() {
            break state.required;
        }

        write_subset(path, &state.required);
        print!("Only the {} required {unit}(s) — ok? [g/b/u]: ", state.required.len());
        std::io::stdout().flush().unwrap();
        let Some(answer) = read_answer(&mut history, &mut state, restore_on_ctrlc, original_content, path) else {
            continue;
        };
        if !answer {
            record_broken(&mut smallest_broken, &state.required, &copy_path, write_subset);
            break state.required;
        }
        state.lo = 0;
        state.hi = state.pool.len();
    };

    println!("\nMinimal culprit set ({} {unit}(s)):", culprits.len());
    for c in &culprits {
        println!("    {}", describe(c));
    }

    // `u` pops the previous state and returns None so the caller re-runs that step
    fn read_answer<T: Clone>(
        history: &mut Vec<State<T>>,
        state: &mut State<T>,
        restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
        original_content: &str,
        path: &str,
    ) -> Option<bool> {
        match read_ynu() {
            None => ctrlc_exit(restore_on_ctrlc, original_content, path),
            Some(Err(())) => {
                match history.pop() {
                    Some(prev) => {
                        println!("u (undo)");
                        *state = prev;
                    }
                    None => println!("u (nothing to undo)"),
                }
                None
            }
            Some(Ok(answer)) => {
                println!("{}", if answer { 'g' } else { 'b' });
                history.push(state.clone());
                Some(answer)
            }
        }
    }

    restore_on_ctrlc.lock().unwrap().take();
    std::fs::write(path, original_content).expect("failed to restore original");
    println!("Original savestate restored.");
    if let Some(min) = &smallest_broken {
        println!(
            "Smallest reproducing savestate ({} {unit}(s)) saved to:\n  {copy_path}",
            min.len()
        );
    }
}

// If `subset` is smaller than the smallest broken set seen so far (or the first), record it and
// write it out as a loadable savestate to `copy_path`. Writing eagerly keeps the best-so-far on
// disk even if the run is aborted.
fn record_broken<T: Clone>(
    smallest: &mut Option<Vec<T>>,
    subset: &[T],
    copy_path: &str,
    write_subset: &impl Fn(&str, &[T]),
) {
    let better = smallest.as_ref().map_or(true, |cur| subset.len() < cur.len());
    if better {
        *smallest = Some(subset.to_vec());
        write_subset(copy_path, subset);
    }
}

// Path for the minimal reproducing savestate, in the current working directory (not next to the
// original): `…/foo.json` -> `foo.min-broken.json`.
fn min_broken_path(path: &str) -> String {
    let base = path.rsplit(['/', '\\']).next().unwrap_or(path);
    let stem = base.strip_suffix(".json").unwrap_or(base);
    format!("{stem}.min-broken.json")
}

fn ctrlc_exit(
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
    path: &str,
) -> ! {
    restore_on_ctrlc.lock().unwrap().take();
    std::fs::write(path, original_content).ok();
    eprintln!("\nRestored original savestate.");
    std::process::exit(130);
}

// Some(Ok(true)) = y, Some(Ok(false)) = n, Some(Err(())) = u, None = Ctrl+C
fn read_ynu() -> Option<Result<bool, ()>> {
    enable_raw_mode().expect("failed to enable raw mode");
    let answer = loop {
        if let Ok(Event::Key(key)) = event::read() {
            if key.kind == KeyEventKind::Press {
                match key.code {
                    KeyCode::Char('g') => break Some(Ok(true)),
                    KeyCode::Char('b') => break Some(Ok(false)),
                    KeyCode::Char('u') => break Some(Err(())),
                    KeyCode::Char('c') if key.modifiers.contains(KeyModifiers::CONTROL) => {
                        break None
                    }
                    KeyCode::Esc => break None,
                    _ => {}
                }
            }
        }
    };
    disable_raw_mode().expect("failed to disable raw mode");
    answer
}

// Wait for any key press. Some(()) = a key was pressed; None = Ctrl+C (caller should restore+exit).
fn read_any() -> Option<()> {
    enable_raw_mode().expect("failed to enable raw mode");
    let result = loop {
        if let Ok(Event::Key(key)) = event::read() {
            if key.kind == KeyEventKind::Press {
                if key.code == KeyCode::Char('c') && key.modifiers.contains(KeyModifiers::CONTROL) {
                    break None;
                }
                break Some(());
            }
        }
    };
    disable_raw_mode().expect("failed to disable raw mode");
    result
}

fn write_json(path: &str, value: &Value) {
    let json = serde_json::to_string_pretty(value).expect("serialization failed");
    std::fs::write(path, json).expect("failed to write test savestate");
}
