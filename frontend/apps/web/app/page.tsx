import { redirect } from 'next/navigation';

/**
 * Inventory is the centre of gravity, so it is the landing screen.
 * "Today" becomes the default once it exists (docs/05-ux-design.md §2.3).
 */
export default function Home() {
  redirect('/inventory');
}
