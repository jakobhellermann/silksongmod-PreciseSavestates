use crossterm::event::{self, Event, KeyCode, KeyEventKind, KeyModifiers};
use crossterm::terminal::{disable_raw_mode, enable_raw_mode};
use serde_json::{Map, Value};
use std::io::Write;
use std::sync::{Arc, Mutex};

fn main() {
    let path = std::env::args()
        .nth(1)
        .expect("usage: savestate-bisect <savestate.json>");
    let component = std::env::args()
        .nth(2)
        .unwrap_or_else(|| "HeroController".to_string());

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

    let snapshots = original["ComponentSnapshots"]
        .as_array()
        .expect("no ComponentSnapshots array");

    let hero_idx = snapshots
        .iter()
        .position(|s| {
            s["Path"]
                .as_str()
                .map_or(false, |p| p.ends_with(&format!("@{component}")))
        })
        .unwrap_or_else(|| panic!("no {component} snapshot found"));

    let all_fields: Vec<(String, Value)> = snapshots[hero_idx]["Data"]
        .as_object()
        .expect("Data is not an object")
        .iter()
        .map(|(k, v)| (k.clone(), v.clone()))
        .collect();

    println!("Bisecting {} fields in {component}", all_fields.len());
    println!("g = good, b = broken, u = undo\n");

    write_test(&path, &original, hero_idx, all_fields.iter());
    print!("Full savestate written. Load and test — ok? [g/b]: ");
    std::io::stdout().flush().unwrap();
    match read_ynu() {
        None => ctrlc_exit(&restore_on_ctrlc, &original_content, &path),
        Some(Ok(true)) => {
            println!("g");
            restore_on_ctrlc.lock().unwrap().take();
            std::fs::write(&path, &original_content).expect("failed to restore original");
            println!("Full savestate is ok — nothing to bisect.");
            return;
        }
        _ => println!("b"),
    }

    // history: (candidates_before_split, discarded_half)
    let mut history: Vec<(Vec<(String, Value)>, Vec<(String, Value)>)> = vec![];
    let mut candidates: Vec<(String, Value)> = all_fields;

    loop {
        if candidates.len() == 1 {
            println!("\nFound culprit: {}", candidates[0].0);
            backtrack_phase(
                &path,
                &original,
                hero_idx,
                &candidates[0],
                &history,
                &restore_on_ctrlc,
                &original_content,
            );
            break;
        }
        if candidates.is_empty() {
            println!("\nNo culprit found — all fields are safe.");
            break;
        }

        let mid = candidates.len() / 2;
        write_test(&path, &original, hero_idx, candidates[..mid].iter());

        print!(
            "[{} remaining] kept first {mid} of {} candidates — ok? [g/b/u]: ",
            candidates.len(),
            candidates.len(),
        );
        std::io::stdout().flush().unwrap();

        let answer = loop {
            match read_ynu() {
                None => ctrlc_exit(&restore_on_ctrlc, &original_content, &path),
                Some(Err(())) => {
                    if let Some((prev, _)) = history.pop() {
                        println!("u (undo)");
                        candidates = prev;
                        let mid = candidates.len() / 2;
                        write_test(&path, &original, hero_idx, candidates[..mid].iter());
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

        let discarded = if answer {
            candidates[..mid].to_vec() // took second half, discarded first
        } else {
            candidates[mid..].to_vec() // took first half, discarded second
        };
        history.push((candidates.clone(), discarded));

        if answer {
            candidates = candidates[mid..].to_vec();
        } else {
            candidates = candidates[..mid].to_vec();
        }
    }

    restore_on_ctrlc.lock().unwrap().take();
    std::fs::write(&path, &original_content).expect("failed to restore original");
    println!("Original savestate restored.");
}

fn backtrack_phase(
    path: &str,
    original: &Value,
    hero_idx: usize,
    culprit: &(String, Value),
    history: &[(Vec<(String, Value)>, Vec<(String, Value)>)],
    restore_on_ctrlc: &Arc<Mutex<Option<(String, String)>>>,
    original_content: &str,
) {
    // discarded_halves[0] = closest to culprit (last bisect round), [last] = outermost
    let discarded_halves: Vec<&Vec<(String, Value)>> =
        history.iter().rev().map(|(_, d)| d).collect();

    println!("\n--- Context backtrack (g = good, b = broken) ---");

    let mut context: Vec<&(String, Value)> = vec![culprit];

    write_test(path, original, hero_idx, context.iter().copied());
    print!(
        "Culprit alone ({} field) — ok? [g/b]: ",
        context.len()
    );
    std::io::stdout().flush().unwrap();

    for (i, discarded) in discarded_halves.iter().enumerate() {
        let answer = loop {
            match read_ynu() {
                None => ctrlc_exit(restore_on_ctrlc, original_content, path),
                Some(Err(())) => {
                    print!(" (u not supported here) ");
                    std::io::stdout().flush().unwrap();
                }
                Some(Ok(v)) => break v,
            }
        };
        println!("{}", if answer { 'g' } else { 'b' });

        if !answer {
            // broken at this context level — confirmed culprit interaction here
            println!(
                "Broken with {} context fields (backtrack depth {}/{}).",
                context.len() - 1,
                i,
                discarded_halves.len()
            );
            return;
        }

        // ok so far — add next discarded half and test
        context.extend(discarded.iter());
        write_test(path, original, hero_idx, context.iter().copied());
        print!(
            "Added {} fields (now {} total, depth {}/{}) — ok? [g/b]: ",
            discarded.len(),
            context.len(),
            i + 1,
            discarded_halves.len(),
        );
        std::io::stdout().flush().unwrap();
    }

    // read final answer
    let answer = loop {
        match read_ynu() {
            None => ctrlc_exit(restore_on_ctrlc, original_content, path),
            Some(Err(())) => {
                print!(" (u not supported here) ");
                std::io::stdout().flush().unwrap();
            }
            Some(Ok(v)) => break v,
        }
    };
    println!("{}", if answer { 'g' } else { 'b' });
    if answer {
        println!("Ok even with full context — culprit only breaks in isolation?");
    } else {
        println!("Broken with full context.");
    }
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

fn write_test<'a>(
    path: &str,
    original: &Value,
    hero_idx: usize,
    fields: impl Iterator<Item = &'a (String, Value)>,
) {
    let mut modified = original.clone();
    let data: Map<String, Value> = fields.map(|(k, v)| (k.clone(), v.clone())).collect();
    modified["ComponentSnapshots"][hero_idx]["Data"] = Value::Object(data);
    let json = serde_json::to_string_pretty(&modified).expect("serialization failed");
    std::fs::write(path, json).expect("failed to write test savestate");
}
