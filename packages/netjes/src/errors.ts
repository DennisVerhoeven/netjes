// The mark on errors whose message Netjes withheld: `withheld` is true when the message is Netjes's stand-in for one it
// does not show (error details are off, or the thrown value had none), so `!error.withheld` means it is worth showing.

/** Netjes's stand-in for a message it does not show, marked as one. */
export function withheldError(): Error & { withheld: true } {
  return Object.assign(new Error('Something went wrong'), { withheld: true as const });
}

/** Whether `error` is marked as one whose message was withheld. */
export function isWithheld(error: unknown): boolean {
  return error instanceof Error && (error as { withheld?: unknown }).withheld === true;
}
