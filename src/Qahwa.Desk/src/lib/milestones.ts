/** "3 days", "5 hours", "12 minutes", "under a minute": how long a step took, in the largest whole unit. */
export function durationText(seconds: number): string {
  const units: [string, number][] = [['day', 86400], ['hour', 3600], ['minute', 60]]
  for (const [name, size] of units) {
    if (seconds >= size) {
      const n = Math.floor(seconds / size)
      return `${n} ${name}${n === 1 ? '' : 's'}`
    }
  }
  return 'under a minute'
}
