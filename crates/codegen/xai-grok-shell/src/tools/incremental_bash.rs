//! Incremental bash-output projection for clients that opt in through ACP.

use std::collections::{HashMap, VecDeque};

pub const MAX_TRACKED_BASH_CALLS: usize = 1024;

#[derive(Default)]
pub struct IncrementalBashCursors {
    totals: HashMap<String, usize>,
    background_tools: HashMap<String, String>,
    insertion_order: VecDeque<String>,
}

impl IncrementalBashCursors {
    pub fn previous_total(&self, tool_call_id: &str) -> usize {
        self.totals.get(tool_call_id).copied().unwrap_or(0)
    }

    pub fn record_total(&mut self, tool_call_id: &str, total: usize) {
        if let Some(current) = self.totals.get_mut(tool_call_id) {
            *current = total;
            return;
        }

        if self.totals.len() >= MAX_TRACKED_BASH_CALLS {
            if let Some(oldest) = self.insertion_order.pop_front() {
                self.totals.remove(&oldest);
                self.background_tools
                    .retain(|_, tool_id| tool_id != &oldest);
            }
        }

        self.insertion_order.push_back(tool_call_id.to_owned());
        self.totals.insert(tool_call_id.to_owned(), total);
    }

    pub fn background_task(&mut self, task_id: &str, tool_call_id: &str) {
        if !self.totals.contains_key(tool_call_id) {
            return;
        }

        self.background_tools
            .retain(|_, mapped_tool_id| mapped_tool_id != tool_call_id);
        self.background_tools
            .insert(task_id.to_owned(), tool_call_id.to_owned());
    }

    pub fn complete_tool(&mut self, tool_call_id: &str) {
        self.totals.remove(tool_call_id);
        self.insertion_order.retain(|id| id != tool_call_id);
        self.background_tools
            .retain(|_, mapped_tool_id| mapped_tool_id != tool_call_id);
    }

    pub fn complete_task(&mut self, task_id: &str) {
        if let Some(tool_call_id) = self.background_tools.remove(task_id) {
            self.complete_tool(&tool_call_id);
        }
    }

    #[cfg(test)]
    fn tracked_tool_count(&self) -> usize {
        self.totals.len()
    }

    #[cfg(test)]
    fn tracked_task_count(&self) -> usize {
        self.background_tools.len()
    }
}

/// Return the bytes produced since `previous_total` and the next monotonic
/// cursor. `retained` is the producer's current tail buffer, which can shrink
/// after truncation even though `current_total` only increases.
pub fn output_delta(
    previous_total: usize,
    retained: &[u8],
    current_total: usize,
) -> (Vec<u8>, usize) {
    if current_total <= previous_total {
        return (Vec::new(), previous_total);
    }

    let new_bytes = current_total - previous_total;
    if new_bytes <= retained.len() {
        return (
            retained[retained.len() - new_bytes..].to_vec(),
            current_total,
        );
    }

    let missing_bytes = new_bytes - retained.len();
    let marker = format!("\n\n... (output truncated; {missing_bytes} bytes unavailable) ...\n\n");
    let mut delta = Vec::with_capacity(marker.len() + retained.len());
    delta.extend_from_slice(marker.as_bytes());
    delta.extend_from_slice(retained);
    (delta, current_total)
}

#[cfg(test)]
mod tests {
    use super::{IncrementalBashCursors, MAX_TRACKED_BASH_CALLS};

    #[test]
    fn terminal_outcomes_release_foreground_cursors() {
        let mut cursors = IncrementalBashCursors::default();
        cursors.record_total("complete", 10);
        cursors.record_total("timeout", 20);
        cursors.record_total("failed", 30);

        cursors.complete_tool("complete");
        cursors.complete_tool("timeout");
        cursors.complete_tool("failed");

        assert_eq!(cursors.tracked_tool_count(), 0);
    }

    #[test]
    fn completed_background_task_releases_its_tool_cursor() {
        let mut cursors = IncrementalBashCursors::default();
        cursors.record_total("tool-1", 42);
        cursors.background_task("task-1", "tool-1");

        cursors.complete_task("task-1");

        assert_eq!(cursors.previous_total("tool-1"), 0);
        assert_eq!(cursors.tracked_task_count(), 0);
    }

    #[test]
    fn cursor_tracking_has_a_hard_capacity_limit() {
        let mut cursors = IncrementalBashCursors::default();
        for index in 0..=MAX_TRACKED_BASH_CALLS {
            let tool_id = format!("tool-{index}");
            cursors.record_total(&tool_id, index + 1);
            cursors.background_task(&format!("task-{index}"), &tool_id);
        }

        assert_eq!(cursors.tracked_tool_count(), MAX_TRACKED_BASH_CALLS);
        assert_eq!(cursors.previous_total("tool-0"), 0);
        assert_eq!(cursors.tracked_task_count(), MAX_TRACKED_BASH_CALLS);
    }
}
