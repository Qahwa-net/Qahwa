/** A grey outline of a form while it loads, so the page does not jump when the data arrives. */
export function FormSkeleton() {
  return (
    <div role="status" aria-label="Loading" className="skeleton-form">
      <div className="skeleton line title" />
      <div className="card">
        <div className="form-grid">
          {[0, 1, 2, 3].map((i) => (
            <div key={i} className="field">
              <div className="skeleton line short" />
              <div className="skeleton line input" />
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
