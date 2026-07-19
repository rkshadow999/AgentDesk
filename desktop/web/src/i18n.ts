export type Messages = Readonly<Record<string, string>>;

export function createTranslator(
  fallbackMessages: Messages,
  localizedMessages: Messages
): (key: string) => string {
  return (key) => localizedMessages[key] ?? fallbackMessages[key] ?? key;
}
