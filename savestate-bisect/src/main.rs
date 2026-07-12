use crossterm::event::{self, Event, KeyCode, KeyEventKind, KeyModifiers};
use crossterm::terminal::{disable_raw_mode, enable_raw_mode};
use serde_json::{Map, Value};
use std::io::Write;
use std::sync::{Arc, Mutex};

fn main() {
    let path = std::env::args()
        .nth(1)
        .expect("usage: savestate-bisect <savestate.json> [component]");
    // No component arg -> snapshot mode (which ComponentSnapshots are applied).
    // Component arg     -> data mode (which Data fields of that component).
    let component = std::env::args().nth(2);

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

    match component.as_deref() {
        None => bisect_snapshots(&path, &original, &restore_on_ctrlc, &original_content),
        // Apply only the ComponentSnapshots whose Path contains one of the given substrings, once,
        // for a manual test. Lets you check an exact hypothesis set (e.g. minimize a backtrack
        // result: does the trigger group break *without* the context the backtrack happened to keep?).
        Some("--only") => {
            let patterns = std::env::args()
                .nth(3)
                .expect("usage: savestate-bisect <savestate.json> --only <substr,substr,…>");
            apply_only(&path, &original, &patterns, &restore_on_ctrlc, &original_content);
        }
        Some(component) => bisect_data(&path, &original, component, &restore_on_ctrlc, &original_content),
    }
}

fn apply_only(
    path: &str,
    original: &Value,
    patterns_csv: &str,
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    let patterns: Vec<&str> = patterns_csv
        .split(',')
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .collect();

    let all = original["ComponentSnapshots"]
        .as_array()
        .expect("no ComponentSnapshots array");

    let kept: Vec<Value> = all
        .iter()
        .filter(|s| {
            let p = s["Path"].as_str().unwrap_or("");
            patterns.iter().any(|pat| p.contains(pat))
        })
        .cloned()
        .collect();

    println!("Applying only {} of {} ComponentSnapshots:", kept.len(), all.len());
    for s in &kept {
        println!("    {}", s["Path"].as_str().unwrap_or("<no Path>"));
    }
    for pat in &patterns {
        if !all.iter().any(|s| s["Path"].as_str().unwrap_or("").contains(pat)) {
            println!("  ! no snapshot matched '{pat}'");
        }
    }

    let mut modified = original.clone();
    modified["ComponentSnapshots"] = Value::Array(kept);
    write_json(path, &modified);

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

// Mode 1: bisect which ComponentSnapshots get applied at all, keyed by Path.
fn bisect_snapshots(
    path: &str,
    original: &Value,
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    let snapshots: Vec<Value> = original["ComponentSnapshots"]
        .as_array()
        .expect("no ComponentSnapshots array")
        .clone();

    println!("Bisecting {} ComponentSnapshots (which get applied)", snapshots.len());
    println!("g = good, b = broken, u = undo\n");

    let write_subset = |target: &str, subset: &[Value]| {
        let mut modified = original.clone();
        modified["ComponentSnapshots"] = Value::Array(subset.to_vec());
        write_json(target, &modified);
    };
    let describe = |s: &Value| {
        s["Path"]
            .as_str()
            .map_or_else(|| "<no Path>".to_string(), str::to_string)
    };

    run_bisect(
        path,
        snapshots,
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

    // undo stack: the candidate list before each split, so `u` can step back one round
    let mut history: Vec<Vec<T>> = vec![];
    let mut candidates: Vec<T> = all;

    loop {
        if candidates.len() == 1 {
            println!(
                "\nForward bisect converged to {unit}: {} (single-culprit guess — minimize from the saved min-broken savestate to confirm)",
                describe(&candidates[0])
            );
            break;
        }
        if candidates.is_empty() {
            println!("\nNo culprit found — all {unit}s are safe.");
            break;
        }

        let mid = candidates.len() / 2;
        write_subset(path, &candidates[..mid]);

        print!(
            "[{} remaining] kept first {mid} of {} candidates — ok? [g/b/u]: ",
            candidates.len(),
            candidates.len(),
        );
        std::io::stdout().flush().unwrap();

        let answer = loop {
            match read_ynu() {
                None => ctrlc_exit(restore_on_ctrlc, original_content, path),
                Some(Err(())) => {
                    if let Some(prev) = history.pop() {
                        println!("u (undo)");
                        candidates = prev;
                        let mid = candidates.len() / 2;
                        write_subset(path, &candidates[..mid]);
                        print!(
                            "[{} remaining] kept first {mid} of {} candidates — ok? [g/b/u]: ",
                            candidates.len(),
                            candidates.len(),
                        );
                        std::io::stdout().flush().unwrap();
                    } else {
                        print!(" (nothing to undo) ");
                        std::io::stdout().flush().unwrap();
                    }
                }
                Some(Ok(v)) => break v,
            }
        };

        println!("{}", if answer { 'g' } else { 'b' });

        // the tested subset was candidates[..mid]; a `b` means it reproduces
        if !answer {
            record_broken(&mut smallest_broken, &candidates[..mid], &copy_path, write_subset);
        }

        history.push(candidates.clone());

        if answer {
            candidates = candidates[mid..].to_vec();
        } else {
            candidates = candidates[..mid].to_vec();
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
